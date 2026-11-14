package com.openmediabridge.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.wifi.WifiManager
import android.os.Binder
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import com.openmediabridge.R
import com.openmediabridge.lyrics.CacheHelper
import com.openmediabridge.lyrics.LyricsFetcher
import com.openmediabridge.lyrics.TranslationService
import com.openmediabridge.model.BridgeConfig
import com.openmediabridge.model.LyricsState
import com.openmediabridge.model.LyricsLine
import com.openmediabridge.model.MediaState
import com.openmediabridge.ui.MainActivity
import com.openmediabridge.websocket.CoverArtFetcher
import com.openmediabridge.websocket.OMBWebSocketServer
import kotlinx.coroutines.*
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.concurrent.atomic.AtomicBoolean

private const val TAG = "BridgeService"
private const val NOTIFICATION_ID = 1001
private const val CHANNEL_ID = "openmediabridge_service"

/**
 * Foreground service that:
 * 1. Listens to MediaListenerService for media changes
 * 2. Fetches lyrics via LyricsFetcher
 * 3. Broadcasts OMB protocol via dual WebSocket servers
 * 4. Updates the UI via callback
 */
class BridgeService : LifecycleService() {

    inner class LocalBinder : Binder() {
        fun getService(): BridgeService = this@BridgeService
    }

    private val binder = LocalBinder()

    // Config
    var config = BridgeConfig()
        private set

    // Media state
    var currentMediaState = MediaState()
        private set
    var currentLyricsState = LyricsState()
        private set

    // Callbacks for UI
    var onMediaStateChanged: ((MediaState) -> Unit)? = null
    var onLyricsStateChanged: ((LyricsState) -> Unit)? = null
    var onLogMessage: ((String) -> Unit)? = null
    var onServerStatus: ((String) -> Unit)? = null

    // WebSocket servers
    private var mediaServer: OMBWebSocketServer? = null
    private var lyricsServer: OMBWebSocketServer? = null

    // Lyrics engine
    private lateinit var lyricsFetcher: LyricsFetcher

    // Position tracking (mirrors LyricsService.cs position simulation)
    private var lastKnownPosition = 0L
    private var lastPositionUpdateTime = 0L
    private var isPlaying = false

    // Lyrics timing
    private var lastLyricUpdateTime = 0L
    private var currentLyricText = ""
    private var currentOffset = 0

    // Word sync / modes
    private var wordSyncEnabled = false
    private var offlineMode = false
    private var cjkFilterEnabled = true
    private var plainFallbackEnabled = false

    // Translation (mirrors LyricsService.cs translation state)
    private var translationEnabled = false
    private var translatedLines: List<LyricsLine>? = null
    private var translatedForKey = ""
    private var translatedForSource = ""
    private var translationPending = false
    private var translationTargetLang = "en"
    private var libreTranslateUrl = "https://libretranslate.com"
    private var translationApiKey = ""

    // Last broadcast lyric
    private var lastBroadcastLyric = ""
    private var lastBroadcastTime = 0L

    // Cover art
    private var lastCoverUrl = ""

    // Tick job
    private var tickJob: Job? = null

    // Wake lock
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    // Running flag
    private val running = AtomicBoolean(false)

    override fun onCreate() {
        super.onCreate()
        log("BridgeService created")

        lyricsFetcher = LyricsFetcher(applicationContext)
        translationEnabled = config.translationEnabled
        translationTargetLang = config.translationTargetLang
        libreTranslateUrl = config.libreTranslateUrl
        translationApiKey = config.translationApiKey

        // Connect to MediaListenerService
        MediaListenerService.onMediaChanged = { state ->
            onMediaSessionChanged(state)
        }

        // Request initial broadcast if service already running
        MediaListenerService.instance?.broadcastCurrentState()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)

        if (!running.getAndSet(true)) {
            startForeground()
            acquireWakeLocks()
            startWebSocketServers()
            startTickLoop()
            log("Bridge started — IP: ${getLocalIpAddress()}")
            onServerStatus?.invoke("Running on ${getLocalIpAddress()}")
        }

        return START_STICKY
    }

    override fun onBind(intent: Intent): IBinder {
        super.onBind(intent)
        return binder
    }

    override fun onDestroy() {
        super.onDestroy()
        running.set(false)
        tickJob?.cancel()
        mediaServer?.stop()
        lyricsServer?.stop()
        releaseWakeLocks()
        MediaListenerService.onMediaChanged = null
        log("BridgeService destroyed")
    }

    // ── Media Session Events ──────────────────────────────────────────────────

    private fun onMediaSessionChanged(state: MediaState) {
        val previousTitle = currentMediaState.title
        currentMediaState = state

        // Notify UI
        onMediaStateChanged?.invoke(state)

        // Broadcast media info to WebSocket clients
        broadcastMediaInfo(state)

        // Fetch lyrics if song changed
        if (state.title != previousTitle || lyricsFetcher.needsNewSong(state.title, state.artist)) {
            lifecycleScope.launch(Dispatchers.IO) {
                fetchLyricsForCurrentSong(state)
            }
        }

        // Fetch cover art if song changed
        if (state.title != previousTitle) {
            lifecycleScope.launch(Dispatchers.IO) {
                val coverUrl = CoverArtFetcher.fetchCoverUrl(state.title, state.artist, state.album)
                if (coverUrl != lastCoverUrl) {
                    lastCoverUrl = coverUrl
                    withContext(Dispatchers.Main) {
                        currentMediaState = currentMediaState.copy(coverUrl = coverUrl)
                        onMediaStateChanged?.invoke(currentMediaState)
                    }
                    mediaServer?.broadcastLine("cover", coverUrl)
                }
            }
        }
    }

    private fun broadcastMediaInfo(state: MediaState) {
        mediaServer?.apply {
            broadcastLine("title", state.title)
            broadcastLine("artist", state.artist)
            broadcastLine("album", state.album)
            broadcastLine("source", state.source)
            broadcastLine("dur", state.durationMs.toString())
            broadcastLine("status", state.isPlaying.toString())
            broadcastLine("shuffle", state.shuffle.toString())
            broadcastLine("repeat", state.repeatMode.name.lowercase())
            broadcastLine("pos", state.positionMs.toString())
            if (state.coverUrl.isNotEmpty()) broadcastLine("cover", state.coverUrl)
        }
    }

    // ── Lyrics Fetching ───────────────────────────────────────────────────────

    private suspend fun fetchLyricsForCurrentSong(state: MediaState) {
        if (state.isEmpty) return

        log("Fetching lyrics: ${state.title} - ${state.artist}")

        // Reset
        withContext(Dispatchers.Main) {
            currentLyricsState = LyricsState()
            currentLyricText = ""
            lastBroadcastLyric = ""
            onLyricsStateChanged?.invoke(currentLyricsState)
        }
        // New song (or forced refresh) invalidates any in-flight translation key,
        // but keep showing the old translated lines until the new one is ready
        // (avoids a flash of raw untranslated lyrics - mirrors the Lyrics_Service.cs fix).
        translatedForKey = ""
        translatedForSource = ""
        mediaServer?.broadcastLine("lyricsrc", "searching...")

        lyricsFetcher.fetchLyrics(
            title = state.title,
            artist = state.artist,
            durationMs = state.durationMs.toInt(),
            isBrowser = false
        ) { progress ->
            log(progress)
        }

        val source = lyricsFetcher.currentSource
        withContext(Dispatchers.Main) {
            currentLyricsState = currentLyricsState.copy(
                lines = lyricsFetcher.getCurrentLines(),
                source = source
            )
            onLyricsStateChanged?.invoke(currentLyricsState)
        }
        mediaServer?.broadcastLine("lyricsrc", source)
        lyricsServer?.broadcastLine("lyricsrc", source)

        if (translationEnabled && source != "None") {
            triggerTranslationIfNeeded(sourceChanged = true)
        }

        // Send full lyrics to any connected clients
        val fullText = getFullLyricsTextRespectingTranslation()
        if (fullText.isNotEmpty()) {
            mediaServer?.broadcastLine("fulllyrics", fullText)
        }

        log("Lyrics ready: $source (${lyricsFetcher.getCurrentLines().size} lines)")
    }

    // ── Translation ───────────────────────────────────────────────────────────

    private fun getFullLyricsTextRespectingTranslation(): String {
        val lines = translatedLines
        if (translationEnabled && lines != null && lines.isNotEmpty()) {
            return lines.joinToString("\n") { it.text }
        }
        return lyricsFetcher.getFullLyricsText()
    }

    /**
     * Mirrors LyricsService.cs TriggerTranslationIfNeeded - checks cache first,
     * then translates the current raw lines via LibreTranslate on a background
     * coroutine. sourceChanged=true invalidates a stale key (e.g. a better lyrics
     * source replacing a worse one for the same song) without nulling the
     * currently-displayed translated lines until the replacement is ready.
     */
    private fun triggerTranslationIfNeeded(sourceChanged: Boolean = false) {
        if (!translationEnabled) return

        val artist = currentMediaState.artist
        val title = currentMediaState.title
        val key = "$artist|$title"
        val currentSrc = lyricsFetcher.currentSource

        if (title.isEmpty()) return
        if (translationPending) return

        // Already translated this exact song+source combo
        if (key == translatedForKey && currentSrc == translatedForSource && !sourceChanged) return

        if (sourceChanged && key == translatedForKey) {
            translatedForKey = ""
            translatedForSource = ""
        }

        val cached = CacheHelper.tryLoadTranslated(
            applicationContext, lyricsFetcher.cacheFolder, artist, title, translationTargetLang
        )
        if (cached != null) {
            translatedLines = cached
            translatedForKey = key
            translatedForSource = currentSrc
            log("Translation loaded from cache ($translationTargetLang)")
            return
        }

        val rawLines = lyricsFetcher.getCurrentLines()
        if (rawLines.isEmpty()) return

        translationPending = true
        val capturedKey = key
        val capturedSource = currentSrc
        val capturedArtist = artist
        val capturedTitle = title
        val capturedLines = rawLines.toList()

        lifecycleScope.launch(Dispatchers.IO) {
            try {
                log("Translating ${capturedLines.size} lines → $translationTargetLang...")
                val translated = TranslationService.translateLyrics(
                    capturedLines, translationTargetLang, libreTranslateUrl, translationApiKey
                )
                if ("${currentMediaState.artist}|${currentMediaState.title}" == capturedKey) {
                    translatedLines = translated
                    translatedForKey = capturedKey
                    translatedForSource = capturedSource
                    CacheHelper.saveTranslated(
                        applicationContext, lyricsFetcher.cacheFolder,
                        capturedArtist, capturedTitle, translationTargetLang, translated
                    )
                    log("Translation complete ($translationTargetLang)")
                    mediaServer?.broadcastLine("lyricsrc", lyricsFetcher.currentSource)
                    lyricsServer?.broadcastLine("lyricsrc", lyricsFetcher.currentSource)
                }
            } catch (e: Exception) {
                log("Translation failed: ${e.message}")
            } finally {
                translationPending = false
            }
        }
    }

    // ── Tick Loop ─────────────────────────────────────────────────────────────

    private fun startTickLoop() {
        tickJob = lifecycleScope.launch(Dispatchers.Default) {
            while (isActive && running.get()) {
                tick()
                delay(50) // 20 Hz - same as LyricsService.cs
            }
        }
    }

    private fun tick() {
        val state = currentMediaState
        if (state.isEmpty) return

        // Position simulation (mirrors LyricsService.cs)
        val mediaService = MediaListenerService.instance
        val rawPosition = mediaService?.getCurrentPositionMs() ?: state.positionMs
        val rawWithOffset = rawPosition + currentOffset

        val simulatedPosition: Long
        if (state.isPlaying) {
            if (!isPlaying) {
                isPlaying = true
                lastKnownPosition = rawWithOffset
                lastPositionUpdateTime = System.currentTimeMillis()
            } else {
                val diff = rawWithOffset - lastKnownPosition
                if (diff > 500 || (Math.abs(diff) > 1500 && lyricsFetcher.needsNewSong(state.title, state.artist))) {
                    lastKnownPosition = rawWithOffset
                    lastPositionUpdateTime = System.currentTimeMillis()
                }
            }
            simulatedPosition = lastKnownPosition + (System.currentTimeMillis() - lastPositionUpdateTime)
        } else {
            isPlaying = false
            lastKnownPosition = rawWithOffset
            simulatedPosition = lastKnownPosition
        }

        // Get current lyric (mirrors LyricsService.cs Tick() branching:
        // word sync is skipped once translation has lines ready)
        val formattedLyric: String
        var shouldUpdate: Boolean

        if (wordSyncEnabled && !(translationEnabled && translatedLines != null)) {
            val ws = lyricsFetcher.getCurrentLineWordSync(simulatedPosition)
            shouldUpdate = ws != currentLyricText
            if (shouldUpdate) {
                currentLyricText = ws
                lastLyricUpdateTime = System.currentTimeMillis()
            }
            formattedLyric = currentLyricText
        } else if (translationEnabled) {
            val lines = translatedLines
            if (lines != null) {
                val idx = lines.indexOfLast { it.timeMs <= simulatedPosition }
                val newLine = if (idx >= 0) lines[idx].text else ""
                shouldUpdate = newLine != currentLyricText
                if (shouldUpdate) {
                    currentLyricText = newLine
                    lastLyricUpdateTime = System.currentTimeMillis()
                } else if (state.isPlaying && currentLyricText.isNotEmpty() &&
                    System.currentTimeMillis() - lastLyricUpdateTime > 5000) {
                    currentLyricText = ""
                    shouldUpdate = true
                }
            } else {
                // Translation pending - don't fall through to raw untranslated lyrics
                shouldUpdate = translationPending && currentLyricText.isNotEmpty()
                if (shouldUpdate) currentLyricText = ""
            }
            formattedLyric = currentLyricText
        } else {
            val line = lyricsFetcher.getCurrentLine(simulatedPosition)
            val changed = line != currentLyricText
            if (changed) {
                currentLyricText = line
                lastLyricUpdateTime = System.currentTimeMillis()
            }
            shouldUpdate = changed
            formattedLyric = currentLyricText
        }

        val duration = state.durationMs.toDouble().let { if (it > 0) it else lyricsFetcher.getSongLength().toDouble() }
        val progress = if (duration > 0) (simulatedPosition.toDouble() / duration).coerceIn(0.0, 1.0) else 0.0

        // Broadcast lyric changes + periodic progress
        val now = System.currentTimeMillis()
        if (shouldUpdate) {
            broadcastLyric(formattedLyric, progress)
            lastBroadcastTime = now

            // Update UI state - use translated lines for the on-device lyrics list
            // when translation is active and ready (mirrors what's actually broadcast).
            val sourceLines = if (translationEnabled) translatedLines ?: emptyList() else lyricsFetcher.getCurrentLines()
            val lineIdx = sourceLines.indexOfLast { it.timeMs <= simulatedPosition }
            // If word sync on (and translation isn't overriding display), inject highlighted text into active line for the UI
            val displayLines = if (wordSyncEnabled && !translationEnabled && lineIdx >= 0) {
                sourceLines.mapIndexed { idx, line ->
                    if (idx == lineIdx) line.copy(text = formattedLyric) else line
                }
            } else {
                sourceLines
            }
            val newState = currentLyricsState.copy(
                lines = displayLines,
                source = lyricsFetcher.currentSource,
                currentLineIndex = lineIdx,
                wordSyncEnabled = wordSyncEnabled,
                offsetMs = currentOffset
            )
            if (newState != currentLyricsState) {
                currentLyricsState = newState
                // Post to main thread for UI callback
                lifecycleScope.launch(Dispatchers.Main) {
                    onLyricsStateChanged?.invoke(currentLyricsState)
                }
            }
        } else if (state.isPlaying && now - lastBroadcastTime >= 1000) {
            // Progress-only update every second
            lyricsServer?.broadcastLine("prog", "%.3f".format(progress))
            mediaServer?.broadcastLine("prog", "%.3f".format(progress))
            mediaServer?.broadcastLine("pos", simulatedPosition.toString())
            lastBroadcastTime = now
        }
    }

    private fun broadcastLyric(lyric: String, progress: Double) {
        val progStr = "%.3f".format(progress)
        mediaServer?.apply {
            broadcastLine("lyric", lyric)
            broadcastLine("prog", progStr)
        }
        lyricsServer?.apply {
            broadcastLine("lyric", lyric)
            broadcastLine("prog", progStr)
        }
    }

    // ── Command Handling ──────────────────────────────────────────────────────

    private fun handleCommand(cmd: String) {
        val lower = cmd.trim().lowercase()
        val mediaService = MediaListenerService.instance

        when (lower) {
            "play" -> mediaService?.play()
            "pause" -> mediaService?.pause()
            "next" -> mediaService?.skipNext()
            "prev", "previous" -> mediaService?.skipPrevious()
            "stop" -> mediaService?.stop()

            "toggle:wordsync", "w" -> {
                wordSyncEnabled = !wordSyncEnabled
                lyricsFetcher.let { }
                val v = wordSyncEnabled.toString()
                mediaServer?.broadcastLine("wordsync", v)
                lyricsServer?.broadcastLine("wordsync", v)
                log("Word sync: ${if (wordSyncEnabled) "ON" else "OFF"}")
            }
            "toggle:translation", "t" -> {
                translationEnabled = !translationEnabled
                log("Translation: ${if (translationEnabled) "ON" else "OFF"} ($translationTargetLang)")
                val v = translationEnabled.toString()
                mediaServer?.broadcastLine("translate", v)
                lyricsServer?.broadcastLine("translate", v)
                // If turning off, or turning on with translated lines already ready, refresh templates now
                if (!translationEnabled || translatedLines != null) {
                    mediaServer?.broadcastLine("lyricsrc", lyricsFetcher.currentSource)
                    lyricsServer?.broadcastLine("lyricsrc", lyricsFetcher.currentSource)
                }
                if (translationEnabled) triggerTranslationIfNeeded()
            }
            "toggle:offline", "o" -> {
                offlineMode = !offlineMode
                lyricsFetcher.offlineMode = offlineMode
                log("Offline mode: ${if (offlineMode) "ON" else "OFF"}")
                lyricsFetcher.forceRefetch()
            }
            "toggle:cjk", "c" -> {
                cjkFilterEnabled = !cjkFilterEnabled
                lyricsFetcher.filterCjk = cjkFilterEnabled
                log("CJK filter: ${if (cjkFilterEnabled) "ON" else "OFF"}")
                lyricsFetcher.forceRefetch()
            }
            "toggle:plain", "p" -> {
                plainFallbackEnabled = !plainFallbackEnabled
                lyricsFetcher.plainLyricsFallback = plainFallbackEnabled
                log("Plain fallback: ${if (plainFallbackEnabled) "ON" else "OFF"}")
                lyricsFetcher.forceRefetch()
            }
            "nextlyrics", "n" -> {
                lyricsFetcher.nextLyrics()
                val src = lyricsFetcher.currentSource
                mediaServer?.broadcastLine("lyricsrc", src)
                lyricsServer?.broadcastLine("lyricsrc", src)
            }
            "refresh", "r" -> {
                lyricsFetcher.forceRefetch()
                log("Refreshing lyrics...")
                lifecycleScope.launch(Dispatchers.IO) {
                    fetchLyricsForCurrentSong(currentMediaState)
                }
            }
            "clearcache", "x" -> {
                lyricsFetcher.clearCache(currentMediaState.title, currentMediaState.artist)
                lyricsFetcher.forceRefetch()
                log("Cache cleared")
                lifecycleScope.launch(Dispatchers.IO) {
                    fetchLyricsForCurrentSong(currentMediaState)
                }
            }
            "offset:+50", "+" -> adjustOffset(50)
            "offset:-50", "-" -> adjustOffset(-50)
            "offset:+500" -> adjustOffset(500)
            "offset:-500" -> adjustOffset(-500)
            "getstatus", "status", "?" -> {
                // Resend all state to requesting client
                broadcastMediaInfo(currentMediaState)
                mediaServer?.broadcastLine("lyricsrc", lyricsFetcher.currentSource)
                mediaServer?.broadcastLine("wordsync", wordSyncEnabled.toString())
                mediaServer?.broadcastLine("offset", currentOffset.toString())
                mediaServer?.broadcastLine("translate", translationEnabled.toString())
                mediaServer?.broadcastLine("translatelang", translationTargetLang)
            }
            "getfulllyrics" -> {
                val full = getFullLyricsTextRespectingTranslation()
                mediaServer?.broadcastLine("fulllyrics", full)
                lyricsServer?.broadcastLine("fulllyrics", full)
            }
            else -> {
                if (lower.startsWith("offset:")) {
                    val v = cmd.substring(7).toIntOrNull()
                    if (v != null) adjustOffset(v)
                } else if (lower.startsWith("lang:")) {
                    setTranslationLanguage(cmd.substring(5).trim())
                }
            }
        }
    }

    private fun setTranslationLanguage(langCode: String) {
        if (langCode.isBlank() || langCode.equals(translationTargetLang, ignoreCase = true)) return
        translationTargetLang = langCode.lowercase()
        translatedLines = null
        translatedForKey = ""
        translatedForSource = ""
        log("Translation language: $translationTargetLang")
        mediaServer?.broadcastLine("translatelang", translationTargetLang)
        lyricsServer?.broadcastLine("translatelang", translationTargetLang)
        // Revert templates to original while new translation loads
        mediaServer?.broadcastLine("lyricsrc", lyricsFetcher.currentSource)
        lyricsServer?.broadcastLine("lyricsrc", lyricsFetcher.currentSource)
        if (translationEnabled) triggerTranslationIfNeeded()
    }

    private fun adjustOffset(delta: Int) {
        currentOffset += delta
        mediaServer?.broadcastLine("offset", currentOffset.toString())
        lyricsServer?.broadcastLine("offset", currentOffset.toString())
        log("Offset: ${currentOffset}ms")
    }

    // ── WebSocket Servers ─────────────────────────────────────────────────────

    private fun startWebSocketServers() {
        try {
            mediaServer = OMBWebSocketServer("0.0.0.0", config.port) { cmd ->
                handleCommand(cmd)
            }
            mediaServer?.start()
            log("Media WebSocket started on port ${config.port}")

            lyricsServer = OMBWebSocketServer("0.0.0.0", config.lyricsPort) { cmd ->
                handleCommand(cmd)
            }
            lyricsServer?.start()
            log("Lyrics WebSocket started on port ${config.lyricsPort}")

        } catch (e: Exception) {
            log("Failed to start WebSocket servers: ${e.message}")
        }
    }

    // ── Foreground Notification ───────────────────────────────────────────────

    private fun startForeground() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        val channel = NotificationChannel(
            CHANNEL_ID,
            "OpenMediaBridge",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "Media bridge service"
            setShowBadge(false)
        }
        nm.createNotificationChannel(channel)

        val intent = Intent(this, MainActivity::class.java)
        val pi = PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE)

        val notification = Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("OpenMediaBridge")
            .setContentText("Bridging media to WebSocket clients")
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentIntent(pi)
            .setOngoing(true)
            .build()

        startForeground(NOTIFICATION_ID, notification)
    }

    private fun updateNotification(title: String, artist: String) {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        val intent = Intent(this, MainActivity::class.java)
        val pi = PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE)

        val notification = Notification.Builder(this, CHANNEL_ID)
            .setContentTitle(if (title.isNotEmpty()) title else "OpenMediaBridge")
            .setContentText(if (artist.isNotEmpty()) artist else "No media playing")
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentIntent(pi)
            .setOngoing(true)
            .build()

        nm.notify(NOTIFICATION_ID, notification)
    }

    // ── Wake Locks ────────────────────────────────────────────────────────────

    private fun acquireWakeLocks() {
        val pm = getSystemService(POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(
            PowerManager.PARTIAL_WAKE_LOCK,
            "OpenMediaBridge::BridgeLock"
        ).apply { acquire(10 * 60 * 60 * 1000L) } // 10 hours

        val wm = applicationContext.getSystemService(WIFI_SERVICE) as WifiManager
        wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "OpenMediaBridge::WifiLock")
            .apply { acquire() }
    }

    private fun releaseWakeLocks() {
        try { wakeLock?.release() } catch (e: Exception) {}
        try { wifiLock?.release() } catch (e: Exception) {}
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    fun getLocalIpAddress(): String {
        return try {
            NetworkInterface.getNetworkInterfaces()?.toList()
                ?.flatMap { it.inetAddresses.toList() }
                ?.firstOrNull { !it.isLoopbackAddress && it is Inet4Address }
                ?.hostAddress ?: "unknown"
        } catch (e: Exception) { "unknown" }
    }

    private fun log(msg: String) {
        Log.d(TAG, msg)
        onLogMessage?.invoke(msg)
    }

    // Public API for UI
    fun getServerAddress() = "ws://${getLocalIpAddress()}:${config.port}"
    fun getLyricsServerAddress() = "ws://${getLocalIpAddress()}:${config.lyricsPort}"
    fun getMediaClientCount() = mediaServer?.connectedCount ?: 0
    fun getLyricsClientCount() = lyricsServer?.connectedCount ?: 0
    fun isWordSyncEnabled() = wordSyncEnabled
    fun isOfflineModeEnabled() = offlineMode
    fun isCjkFilterEnabled() = cjkFilterEnabled
    fun isPlainFallbackEnabled() = plainFallbackEnabled
    fun isTranslationEnabled() = translationEnabled
    fun isTranslationPending() = translationPending
    fun getTranslationTargetLang() = translationTargetLang
    fun getCurrentOffset() = currentOffset
    fun getFullLyricsText() = getFullLyricsTextRespectingTranslation()
    fun toggleWordSync() { handleCommand("w") }
    fun toggleTranslation() { handleCommand("t") }
    fun toggleOfflineMode() { handleCommand("o") }
    fun toggleCjkFilter() { handleCommand("c") }
    fun togglePlainFallback() { handleCommand("p") }
    fun selectTranslationLanguage(code: String) { handleCommand("lang:$code") }
    fun increaseOffset() { handleCommand("+") }
    fun decreaseOffset() { handleCommand("-") }
    fun nextLyrics() { handleCommand("n") }
    fun refreshLyrics() { handleCommand("r") }
    fun clearCache() { handleCommand("x") }

    companion object {
        // Mirrors the language list in Lyrics_Service.cs
        val TRANSLATION_LANGUAGES = listOf(
            "en", "ar", "zh", "nl", "fr", "de", "it", "ja", "ko", "pt", "ru", "es"
        )
    }
}
