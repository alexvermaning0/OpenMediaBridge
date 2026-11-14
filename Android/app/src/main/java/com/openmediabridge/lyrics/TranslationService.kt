package com.openmediabridge.lyrics

import com.openmediabridge.model.LyricsLine
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.util.concurrent.TimeUnit

/**
 * Translates lyric lines via a LibreTranslate-compatible endpoint.
 * Mirrors TranslationService.cs - all lines sent in a single request
 * (joined by \n) to minimise API calls; original timestamps are kept.
 */
object TranslationService {
    private val httpClient = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()

    fun translateLyrics(
        lines: List<LyricsLine>,
        targetLang: String,
        endpoint: String,
        apiKey: String = ""
    ): List<LyricsLine> {
        if (lines.isEmpty()) return emptyList()

        val texts = lines.map { it.text.ifBlank { " " } }
        val joined = texts.joinToString("\n")

        val body = JSONObject().apply {
            put("q", joined)
            put("source", "auto")
            put("target", targetLang)
            put("format", "text")
            put("api_key", apiKey)
        }.toString()

        val url = endpoint.trimEnd('/') + "/translate"
        val request = Request.Builder()
            .url(url)
            .post(body.toRequestBody("application/json".toMediaType()))
            .build()

        val responseJson = httpClient.newCall(request).execute().use { resp ->
            if (!resp.isSuccessful) throw java.io.IOException("Translation request failed: ${resp.code}")
            resp.body?.string() ?: ""
        }

        val translatedText = JSONObject(responseJson).optString("translatedText", "")
        val translatedLines = translatedText.split("\n")

        return lines.mapIndexed { i, line ->
            LyricsLine(
                timeMs = line.timeMs,
                text = if (i < translatedLines.size) translatedLines[i].trim() else line.text
            )
        }
    }
}
