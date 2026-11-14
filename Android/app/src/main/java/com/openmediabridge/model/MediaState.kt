package com.openmediabridge.model

/**
 * Mirrors the media state broadcast by OMB on Windows.
 * Populated from Android MediaSession / NotificationListenerService.
 */
data class MediaState(
    val title: String = "",
    val artist: String = "",
    val album: String = "",
    val source: String = "",          // e.g. "Spotify", "YouTube Music"
    val coverUrl: String = "",        // public URL (iTunes/Deezer)
    val coverBitmap: android.graphics.Bitmap? = null,
    val durationMs: Long = 0L,
    val positionMs: Long = 0L,
    val isPlaying: Boolean = false,
    val shuffle: Boolean = false,
    val repeatMode: RepeatMode = RepeatMode.NONE,
    val packageName: String = ""      // source app package
) {
    val isEmpty get() = title.isEmpty() && artist.isEmpty()
}

enum class RepeatMode { NONE, TRACK, LIST }

/**
 * Current lyrics state - mirrors LyricsService on Windows.
 */
data class LyricsState(
    val lines: List<LyricsLine> = emptyList(),
    val source: String = "None",
    val isEstimated: Boolean = false,
    val isPlain: Boolean = false,
    val currentLineIndex: Int = -1,
    val wordSyncEnabled: Boolean = false,
    val offsetMs: Int = 0
) {
    val hasLyrics get() = lines.isNotEmpty()
    val currentLine get() = lines.getOrNull(currentLineIndex)
}

data class LyricsLine(
    val timeMs: Long,
    val text: String
)

/**
 * App config - persisted to SharedPreferences.
 */
data class BridgeConfig(
    val port: Int = 8080,
    val lyricsPort: Int = 6555,
    val offsetMs: Int = 0,
    val filterCjk: Boolean = true,
    val offlineMode: Boolean = false,
    val plainLyricsFallback: Boolean = false,
    val wordSync: Boolean = false,
    val cacheFolder: String = "lyrics_cache",
    val ignoredPackages: List<String> = emptyList(),
    val translationEnabled: Boolean = false,
    val translationTargetLang: String = "en",
    val libreTranslateUrl: String = "https://translate.minekingshosting.nl",
    val translationApiKey: String = ""
)
