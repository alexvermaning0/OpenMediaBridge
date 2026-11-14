package com.openmediabridge.lyrics

import android.content.Context
import android.util.Log
import com.openmediabridge.model.LyricsLine
import com.openmediabridge.model.LyricsState
import kotlinx.coroutines.*
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import java.net.URLEncoder
import java.security.MessageDigest
import java.io.File
import java.util.concurrent.TimeUnit
import org.json.JSONObject

private const val TAG = "LyricsFetcher"

/**
 * Full lyrics result from a single source.
 */
data class LyricsResult(
    val lines: List<LyricsLine>,
    val source: String,
    val artist: String = "",
    val title: String = "",
    val album: String = "",
    val isEstimated: Boolean = false,
    val isPlain: Boolean = false
)

/**
 * Main lyrics orchestrator - mirrors LyricsFetcher.cs on Windows.
 * Tries: cache → local DB → LRCLib → NetEase
 */
class LyricsFetcher(
    private val context: Context,
    var cacheFolder: String = "lyrics_cache",
    var filterCjk: Boolean = true,
    var offlineMode: Boolean = false,
    var plainLyricsFallback: Boolean = false
) {
    private val httpClient = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(8, TimeUnit.SECONDS)
        .build()

    private var allResults = mutableListOf<LyricsResult>()
    private var currentIndex = 0
    private var currentTitle = ""
    private var currentArtist = ""
    private var fetchJob: Job? = null

    val currentSource get() = allResults.getOrNull(currentIndex)?.source ?: "None"
    val totalResults get() = allResults.size
    val hasMultiple get() = allResults.size > 1

    fun needsNewSong(title: String, artist: String): Boolean {
        return !title.equals(currentTitle, ignoreCase = true) ||
               !artist.equals(currentArtist, ignoreCase = true)
    }

    fun forceRefetch() {
        currentTitle = ""
        currentArtist = ""
        allResults.clear()
        currentIndex = 0
    }

    fun nextLyrics() {
        if (allResults.size <= 1) return
        currentIndex = (currentIndex + 1) % allResults.size
        Log.d(TAG, "Switched to lyrics ${currentIndex + 1}/${allResults.size}: ${currentSource}")
    }

    fun previousLyrics() {
        if (allResults.size <= 1) return
        currentIndex = (currentIndex - 1 + allResults.size) % allResults.size
    }

    /**
     * Fetch lyrics for a song. Runs synchronously (call from coroutine).
     */
    suspend fun fetchLyrics(
        title: String,
        artist: String,
        durationMs: Int = 0,
        isBrowser: Boolean = false,
        onProgress: ((String) -> Unit)? = null
    ) {
        currentTitle = title
        currentArtist = artist
        allResults.clear()
        currentIndex = 0

        val minScore = if (isBrowser) 200 else 800

        fun addResult(result: LyricsResult) {
            if (result.lines.isEmpty()) return
            val fp = getFingerprint(result.lines)
            if (fp.isEmpty()) return

            val score = scoreResult(result, title, artist, durationMs)
            if (result.source != "cache" && result.source != "localdb" && score < minScore) {
                Log.d(TAG, "✗ Rejected ${result.source} (score=$score < $minScore)")
                return
            }

            // Deduplicate
            for (existing in allResults) {
                if (getFingerprint(existing.lines) == fp) return
            }

            allResults.add(result)

            if (allResults.size == 1) {
                currentIndex = 0
                Log.d(TAG, "✓ Using ${result.source} (score=$score)")
            } else {
                val currentScore = scoreResult(allResults[currentIndex], title, artist, durationMs)
                if (score > currentScore) {
                    currentIndex = allResults.size - 1
                    Log.d(TAG, "✓ Better match ${result.source} (score=$score > $currentScore)")
                } else {
                    Log.d(TAG, "✓ Alt found ${result.source} (score=$score)")
                }
            }
        }

        // 1. Cache
        onProgress?.invoke("Checking cache...")
        val cached = CacheHelper.tryLoad(context, cacheFolder, artist, title)
        if (cached != null) {
            addResult(LyricsResult(lines = cached, source = "cache", artist = artist, title = title))
        }

        // 2. LRCLib API
        if (!offlineMode) {
            onProgress?.invoke("Searching LRCLib...")
            val lrclibResults = LRCLibFetcher.getAllLyrics(httpClient, title, artist, durationMs, filterCjk)
            lrclibResults.forEach { addResult(it) }
        }

        // 3. NetEase (fallback)
        if (!offlineMode && allResults.isEmpty()) {
            onProgress?.invoke("Trying NetEase...")
            val netease = NetEaseFetcher.getLyrics(httpClient, title, artist, filterCjk)
            if (netease != null) addResult(netease)
        }

        // 4. Plain lyrics fallback
        if (!offlineMode && plainLyricsFallback) {
            onProgress?.invoke("Trying plain lyrics...")
            val plain = LRCLibFetcher.getPlainLyrics(httpClient, title, artist, durationMs, filterCjk)
            plain.forEach { addResult(it) }
        }

        // Cache best result
        if (allResults.isNotEmpty() && allResults[currentIndex].source != "cache") {
            CacheHelper.save(context, cacheFolder, artist, title,
                allResults[currentIndex].lines, allResults[currentIndex].source)
        }

        if (allResults.isEmpty()) {
            onProgress?.invoke("No lyrics found")
        } else {
            onProgress?.invoke("Found: ${currentSource}")
        }
    }

    fun getCurrentLines(): List<LyricsLine> {
        return allResults.getOrNull(currentIndex)?.lines ?: emptyList()
    }

    fun getCurrentLine(positionMs: Long): String {
        val lines = getCurrentLines()
        if (lines.isEmpty()) return ""
        val idx = lines.indexOfLast { it.timeMs <= positionMs }
        if (idx < 0) return ""
        return lines[idx].text
    }

    /**
     * Word-by-word sync - mirrors LyricsFetcher.GetCurrentLineWordSync
     */
    fun getCurrentLineWordSync(positionMs: Long): String {
        val lines = getCurrentLines()
        if (lines.isEmpty()) return ""

        val idx = lines.indexOfLast { it.timeMs <= positionMs }
        if (idx < 0) return ""
        if (idx >= lines.size - 1) return lines[idx].text

        val current = lines[idx]
        val next = lines[idx + 1]
        val interval = next.timeMs - current.timeMs
        if (interval <= 0) return current.text

        val LONG_PAUSE_MS = 2000L
        val SPOKEN_CAP = 0.75
        val MAX_WORD_MS = 500.0
        val MIN_WORD_MS = 120.0
        val COMMA_PAUSE = 150
        val MID_PAUSE = 180
        val STOP_PAUSE = 250

        val text = current.text
        val tokens = tokenize(text)
        if (tokens.isEmpty()) return ""

        fun weight(s: String) = s.count { it.isLetterOrDigit() }.coerceAtLeast(1)
        val weights = tokens.map { weight(it) }
        val totalWeight = weights.sum().coerceAtLeast(1)

        val allowedMs: Double = if (interval >= LONG_PAUSE_MS) {
            val capPortion = (interval * SPOKEN_CAP)
            val capToken = tokens.size * MAX_WORD_MS
            minOf(capPortion, capToken).coerceAtLeast(tokens.size * MIN_WORD_MS)
        } else interval.toDouble()

        val idealDurs = weights.map { allowedMs * (it.toDouble() / totalWeight) }
        val highlightDurs = idealDurs.map { it.coerceIn(MIN_WORD_MS, MAX_WORD_MS) }.toMutableList()

        fun pauseFor(token: String): Int {
            val last = token.lastOrNull() ?: return 0
            return when (last) {
                ',' -> COMMA_PAUSE
                ';', ':' -> MID_PAUSE
                '.', '!', '?' -> STOP_PAUSE
                else -> 0
            }
        }

        val pauses = tokens.map { pauseFor(it) }
        val baseSum = highlightDurs.sum()
        val pauseSum = pauses.sum()
        val totalWithPauses = baseSum + pauseSum

        if (totalWithPauses > allowedMs && baseSum > 0) {
            val scale = ((allowedMs - pauseSum) / baseSum).coerceIn(0.2, 1.0)
            for (i in highlightDurs.indices) highlightDurs[i] *= scale
        }

        // Build cumulative segment ends
        val segEnds = DoubleArray(tokens.size * 2)
        var seg = 0
        var acc = 0.0
        for (i in tokens.indices) {
            acc += highlightDurs[i]
            segEnds[seg++] = acc
            acc += pauses[i]
            segEnds[seg++] = acc
        }

        val elapsed = (positionMs - current.timeMs).toDouble()
        if (elapsed < 0) return ""
        val finalTotal = highlightDurs.sum() + pauseSum
        if (elapsed >= finalTotal) return tokens.mapIndexed { i, token ->
            if (i == tokens.size - 1) "<color=yellow>$token</color>" else token
        }.joinToString(" ")

        val segIdx = segEnds.indexOfFirst { elapsed <= it }.let { if (it < 0) segEnds.size - 1 else it }
        val tokenIdx = (segIdx / 2).coerceAtMost(tokens.size - 1)

        return tokens.mapIndexed { i, token ->
            if (i == tokenIdx) "<color=yellow>$token</color>" else token
        }.joinToString(" ")
    }

    fun getFullLyricsText(): String {
        val lines = getCurrentLines()
        return lines.joinToString("\n") { it.text }
    }

    fun getSongLength(): Long = getCurrentLines().lastOrNull()?.timeMs ?: 0L

    fun clearCache(title: String, artist: String) {
        CacheHelper.clear(context, cacheFolder, artist, title)
    }

    private fun getFingerprint(lines: List<LyricsLine>): String {
        return lines.filter { it.text.isNotBlank() }
            .take(5)
            .joinToString("|") { it.text.lowercase().trim() }
    }

    private fun scoreResult(result: LyricsResult, targetTitle: String, targetArtist: String, targetDurationMs: Int): Int {
        var score = when (result.source.lowercase()) {
            "cache" -> 50
            "localdb" -> 40
            "lrclib" -> 30
            "lrclib (local)" -> 40
            "netease" -> 20
            else -> 10
        }
        score += (getSimilarity(result.title, targetTitle) * 1000).toInt()
        score += (getSimilarity(result.artist, targetArtist) * 500).toInt()

        if (targetDurationMs > 0 && result.lines.isNotEmpty()) {
            val lastTime = result.lines.last().timeMs
            if (lastTime > 0) {
                val ratio = minOf(lastTime, targetDurationMs.toLong()).toDouble() /
                            maxOf(lastTime, targetDurationMs.toLong())
                score += (ratio * 500).toInt()
            }
        }
        score += result.lines.size.coerceAtMost(100)
        if (result.isEstimated) score -= 200
        if (result.isPlain) score -= 300
        return score
    }

    private fun getSimilarity(a: String, b: String): Double {
        if (a.isEmpty() || b.isEmpty()) return 0.0
        val al = a.lowercase().trim()
        val bl = b.lowercase().trim()
        if (al == bl) return 1.0
        if (al.contains(bl) || bl.contains(al)) return 0.8
        val wordsA = al.split(Regex("[ \\-_()\\[\\]]")).filter { it.isNotEmpty() }
        val wordsB = bl.split(Regex("[ \\-_()\\[\\]]")).filter { it.isNotEmpty() }
        if (wordsA.isEmpty() || wordsB.isEmpty()) return 0.0
        val matches = wordsA.count { wa -> wordsB.any { wb -> wb.contains(wa) || wa.contains(wb) } }
        return matches.toDouble() / maxOf(wordsA.size, wordsB.size)
    }

    private fun isCjk(c: Char) = c in '\u4E00'..'\u9FFF' ||
            c in '\u3040'..'\u309F' || c in '\u30A0'..'\u30FF' || c in '\uAC00'..'\uD7AF'

    private fun tokenize(text: String): List<String> {
        if (text.none { isCjk(it) }) return text.split(' ').filter { it.isNotEmpty() }
        val tokens = mutableListOf<String>()
        val buf = StringBuilder()
        for (c in text) {
            when {
                c.isWhitespace() -> { if (buf.isNotEmpty()) { tokens.add(buf.toString()); buf.clear() } }
                isCjk(c) -> { if (buf.isNotEmpty()) { tokens.add(buf.toString()); buf.clear() }; tokens.add(c.toString()) }
                else -> buf.append(c)
            }
        }
        if (buf.isNotEmpty()) tokens.add(buf.toString())
        return tokens
    }
}

// ─── LRC Parser (shared) ──────────────────────────────────────────────────────

object LrcParser {
    private val TIMESTAMP = Regex("""^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)""")

    fun parse(lrc: String): List<LyricsLine> {
        return lrc.lines()
            .mapNotNull { line ->
                val m = TIMESTAMP.find(line) ?: return@mapNotNull null
                val min = m.groupValues[1].toInt()
                val sec = m.groupValues[2].toInt()
                val ms = m.groupValues[3].padEnd(3, '0').toInt()
                val text = m.groupValues[4].trim()
                if (text.isEmpty()) null
                else LyricsLine(timeMs = (min * 60000 + sec * 1000 + ms).toLong(), text = text)
            }
            .sortedBy { it.timeMs }
    }

    fun isMostlyCjk(text: String): Boolean {
        val relevant = text.filter { it.isLetterOrDigit() || it.code > 127 }
        if (relevant.isEmpty()) return false
        val cjk = relevant.count {
            it in '\u4E00'..'\u9FFF' || it in '\u3040'..'\u309F' ||
            it in '\u30A0'..'\u30FF' || it in '\uAC00'..'\uD7AF'
        }
        return cjk.toDouble() / relevant.length > 0.3
    }
}

// ─── LRCLib Fetcher ───────────────────────────────────────────────────────────

object LRCLibFetcher {
    private const val BASE = "https://lrclib.net/api"

    fun getAllLyrics(
        client: OkHttpClient,
        title: String,
        artist: String,
        durationMs: Int,
        filterCjk: Boolean
    ): List<LyricsResult> {
        val results = mutableListOf<LyricsResult>()

        val urls = buildList {
            add("$BASE/search?track_name=${enc(title)}&artist_name=${enc(artist)}")
            if (artist.isNotEmpty()) add("$BASE/search?track_name=${enc(title)}")
            add("$BASE/search?q=${enc("$artist $title".trim())}")
        }

        for (url in urls) {
            if (results.isNotEmpty()) break
            try {
                val json = get(client, url) ?: continue
                val arr = JSONArray(json)
                for (i in 0 until arr.length().coerceAtMost(5)) {
                    val obj = arr.getJSONObject(i)
                    if (obj.optBoolean("instrumental")) continue
                    val synced = obj.optString("syncedLyrics")
                    if (synced.isBlank()) continue
                    if (filterCjk && LrcParser.isMostlyCjk(synced)) continue
                    val lines = LrcParser.parse(synced)
                    if (lines.isNotEmpty()) {
                        results.add(LyricsResult(
                            lines = lines,
                            source = "lrclib",
                            artist = obj.optString("artistName", artist),
                            title = obj.optString("trackName", title),
                            album = obj.optString("albumName", "")
                        ))
                    }
                }
            } catch (e: Exception) {
                Log.w("LRCLibFetcher", "Error: ${e.message}")
            }
        }
        return results
    }

    fun getPlainLyrics(
        client: OkHttpClient,
        title: String,
        artist: String,
        durationMs: Int,
        filterCjk: Boolean
    ): List<LyricsResult> {
        val results = mutableListOf<LyricsResult>()
        try {
            val url = "$BASE/search?track_name=${enc(title)}&artist_name=${enc(artist)}"
            val json = get(client, url) ?: return results
            val arr = JSONArray(json)
            for (i in 0 until arr.length()) {
                val obj = arr.getJSONObject(i)
                if (obj.optBoolean("instrumental")) continue
                val synced = obj.optString("syncedLyrics")
                if (synced.isNotBlank()) continue // already have synced above
                val plain = obj.optString("plainLyrics")
                if (plain.isBlank()) continue
                if (filterCjk && LrcParser.isMostlyCjk(plain)) continue
                val lines = parsePlain(plain, durationMs)
                if (lines.isNotEmpty()) {
                    results.add(LyricsResult(
                        lines = lines, source = "lrclib",
                        artist = obj.optString("artistName", artist),
                        title = obj.optString("trackName", title),
                        isEstimated = true, isPlain = true
                    ))
                }
            }
        } catch (e: Exception) { Log.w("LRCLibFetcher", "Plain error: ${e.message}") }
        return results
    }

    private fun parsePlain(plain: String, durationMs: Int): List<LyricsLine> {
        val lines = plain.lines().map { it.trim() }.filter { it.isNotEmpty() && !isSectionMarker(it) }
        if (lines.isEmpty()) return emptyList()
        val totalChars = lines.sumOf { it.length.coerceAtLeast(1) }
        val startBuffer = (durationMs * 0.05).toInt()
        val available = (durationMs * 0.85).toInt().coerceAtLeast(1000)
        var time = startBuffer.toLong()
        return lines.mapNotNull { line ->
            if (time > durationMs - 500) return@mapNotNull null
            val lineMs = (available * line.length.toDouble() / totalChars)
                .toInt().coerceIn(1500, 8000)
            val l = LyricsLine(timeMs = time, text = line)
            time += lineMs + 200
            l
        }
    }

    private fun isSectionMarker(s: String): Boolean {
        val l = s.lowercase()
        val markers = listOf("verse", "chorus", "bridge", "outro", "intro", "hook",
            "pre-chorus", "refrain", "interlude", "instrumental", "solo")
        if (markers.any { l == it || l.startsWith("$it ") }) return true
        if (l.startsWith("[") && l.endsWith("]")) return true
        return false
    }

    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8")

    private fun get(client: OkHttpClient, url: String): String? {
        val req = Request.Builder().url(url).header("User-Agent", "OpenMediaBridge-Android").build()
        return client.newCall(req).execute().use { resp ->
            if (resp.isSuccessful) resp.body?.string() else null
        }
    }
}

// ─── NetEase Fetcher ──────────────────────────────────────────────────────────

object NetEaseFetcher {
    fun getLyrics(client: OkHttpClient, title: String, artist: String, filterCjk: Boolean): LyricsResult? {
        return try {
            val query = enc("$title-$artist")
            val searchUrl = "https://music.163.com/api/search/get?s=$query&type=1&limit=1"
            val req = Request.Builder()
                .url(searchUrl)
                .header("Referer", "https://music.163.com")
                .header("Cookie", "appver=2.0.2")
                .build()
            val json = client.newCall(req).execute().use { it.body?.string() } ?: return null

            val idMatch = Regex(""""id":(\d+)""").find(json) ?: return null
            val id = idMatch.groupValues[1]

            val lyricsUrl = "https://music.163.com/api/song/lyric?os=pc&id=$id&lv=-1&kv=-1&tv=-1"
            val lyricsReq = Request.Builder().url(lyricsUrl)
                .header("Referer", "https://music.163.com")
                .header("Cookie", "appver=2.0.2")
                .build()
            val lyricsJson = client.newCall(lyricsReq).execute().use { it.body?.string() } ?: return null

            val lrcMatch = Regex(""""lyric":"(.*?)"""", RegexOption.DOT_MATCHES_ALL).find(lyricsJson) ?: return null
            val lrc = lrcMatch.groupValues[1].replace("\\n", "\n")
            if (filterCjk && LrcParser.isMostlyCjk(lrc)) return null

            val lines = LrcParser.parse(lrc)
            if (lines.isEmpty()) return null

            LyricsResult(lines = lines, source = "netease", artist = artist, title = title)
        } catch (e: Exception) {
            Log.w("NetEaseFetcher", "Error: ${e.message}")
            null
        }
    }

    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8")
}

// ─── Cache Helper ─────────────────────────────────────────────────────────────

object CacheHelper {
    private fun getCacheFile(context: Context, folder: String, artist: String, title: String): File {
        val dir = File(context.filesDir, folder).also { it.mkdirs() }
        val sanitize = { s: String -> s.replace(Regex("[^a-zA-Z0-9_\\-]"), "_") }
        val hash = sha1("$artist|$title").take(8)
        return File(dir, "${sanitize(artist)}-${sanitize(title)}-$hash.json")
    }

    fun tryLoad(context: Context, folder: String, artist: String, title: String): List<LyricsLine>? {
        return try {
            val file = getCacheFile(context, folder, artist, title)
            if (!file.exists()) return null
            val obj = JSONObject(file.readText())
            val arr = obj.getJSONArray("lines")
            (0 until arr.length()).map { i ->
                val line = arr.getJSONObject(i)
                LyricsLine(timeMs = line.getLong("time"), text = line.getString("text"))
            }.takeIf { it.isNotEmpty() }
        } catch (e: Exception) { null }
    }

    fun save(context: Context, folder: String, artist: String, title: String,
             lines: List<LyricsLine>, source: String) {
        try {
            val file = getCacheFile(context, folder, artist, title)
            val arr = JSONArray()
            lines.forEach { line ->
                arr.put(JSONObject().apply {
                    put("time", line.timeMs)
                    put("text", line.text)
                })
            }
            val obj = JSONObject().apply {
                put("artist", artist)
                put("title", title)
                put("source", source)
                put("lines", arr)
            }
            file.writeText(obj.toString(2))
        } catch (e: Exception) { Log.e("CacheHelper", "Save failed", e) }
    }

    fun clear(context: Context, folder: String, artist: String, title: String) {
        try { getCacheFile(context, folder, artist, title).delete() } catch (e: Exception) {}
    }

    private fun getTranslatedCacheFile(context: Context, folder: String, artist: String, title: String, lang: String): File {
        val dir = File(context.filesDir, folder).also { it.mkdirs() }
        val sanitize = { s: String -> s.replace(Regex("[^a-zA-Z0-9_\\-]"), "_") }
        val hash = sha1("$artist|$title").take(8)
        return File(dir, "${sanitize(artist)}-${sanitize(title)}-$hash.$lang.json")
    }

    fun tryLoadTranslated(context: Context, folder: String, artist: String, title: String, lang: String): List<LyricsLine>? {
        return try {
            val file = getTranslatedCacheFile(context, folder, artist, title, lang)
            if (!file.exists()) return null
            val obj = JSONObject(file.readText())
            val arr = obj.getJSONArray("lines")
            (0 until arr.length()).map { i ->
                val line = arr.getJSONObject(i)
                LyricsLine(timeMs = line.getLong("time"), text = line.getString("text"))
            }.takeIf { it.isNotEmpty() }
        } catch (e: Exception) { null }
    }

    fun saveTranslated(context: Context, folder: String, artist: String, title: String, lang: String, lines: List<LyricsLine>) {
        try {
            val file = getTranslatedCacheFile(context, folder, artist, title, lang)
            val arr = JSONArray()
            lines.forEach { line ->
                arr.put(JSONObject().apply {
                    put("time", line.timeMs)
                    put("text", line.text)
                })
            }
            val obj = JSONObject().apply {
                put("artist", artist)
                put("title", title)
                put("source", "translated-$lang")
                put("lines", arr)
            }
            file.writeText(obj.toString(2))
        } catch (e: Exception) { Log.e("CacheHelper", "SaveTranslated failed", e) }
    }

    private fun sha1(input: String): String {
        val bytes = MessageDigest.getInstance("SHA-1").digest(input.toByteArray())
        return bytes.joinToString("") { "%02x".format(it) }
    }
}
