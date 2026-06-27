package com.openmediabridge.service

import android.content.ComponentName
import android.content.Intent
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Log
import com.openmediabridge.model.MediaState
import com.openmediabridge.model.RepeatMode
class MediaListenerService : NotificationListenerService() {

    companion object {
        private const val TAG = "MediaListenerService"
        var instance: MediaListenerService? = null

        // Broadcast intent action for media changes
        const val ACTION_MEDIA_CHANGED = "com.openmediabridge.MEDIA_CHANGED"

        // Callback for BridgeService
        var onMediaChanged: ((MediaState) -> Unit)? = null
    }

    private var mediaSessionManager: MediaSessionManager? = null
    private var activeControllers: List<MediaController> = emptyList()
    private var currentController: MediaController? = null
    private val handler = Handler(Looper.getMainLooper())

    // Callbacks registered on active controllers
    private val controllerCallbacks = mutableMapOf<String, MediaController.Callback>()

    // Ignored packages (e.g. system UI)
    private val ignoredPackages = setOf(
        "com.android.systemui",
        "com.google.android.googlequicksearchbox"
    )

    override fun onCreate() {
        super.onCreate()
        instance = this
        Log.d(TAG, "MediaListenerService created")
    }

    override fun onListenerConnected() {
        super.onListenerConnected()
        Log.d(TAG, "Notification listener connected")

        try {
            mediaSessionManager = getSystemService(MEDIA_SESSION_SERVICE) as MediaSessionManager
            refreshMediaSessions()

            // Listen for session changes
            mediaSessionManager?.addOnActiveSessionsChangedListener(
                { controllers -> onActiveSessionsChanged(controllers) },
                ComponentName(this, MediaListenerService::class.java)
            )
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize media session manager", e)
        }
    }

    override fun onListenerDisconnected() {
        super.onListenerDisconnected()
        Log.d(TAG, "Notification listener disconnected")
        clearAllCallbacks()
        instance = null
    }

    override fun onDestroy() {
        super.onDestroy()
        clearAllCallbacks()
        instance = null
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        // We rely on MediaSession API rather than notifications directly
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        // We rely on MediaSession API rather than notifications directly
    }

    private fun onActiveSessionsChanged(controllers: List<MediaController>?) {
        Log.d(TAG, "Active sessions changed: ${controllers?.size ?: 0}")
        clearAllCallbacks()
        refreshMediaSessions()
    }

    private fun refreshMediaSessions() {
        try {
            val component = ComponentName(this, MediaListenerService::class.java)
            val controllers = mediaSessionManager?.getActiveSessions(component) ?: emptyList()

            activeControllers = controllers.filter { it.packageName !in ignoredPackages }
            Log.d(TAG, "Active media controllers: ${activeControllers.map { it.packageName }}")

            // Register callbacks on each controller
            for (controller in activeControllers) {
                registerCallback(controller)
            }

            // Pick the best playing session
            updateCurrentController()
        } catch (e: Exception) {
            Log.e(TAG, "Failed to refresh media sessions", e)
        }
    }

    private fun registerCallback(controller: MediaController) {
        val pkg = controller.packageName
        if (pkg in controllerCallbacks) return // already registered

        val callback = object : MediaController.Callback() {
            override fun onMetadataChanged(metadata: MediaMetadata?) {
                Log.d(TAG, "[$pkg] Metadata changed: ${metadata?.getString(MediaMetadata.METADATA_KEY_TITLE)}")
                if (controller == currentController || isPlaying(controller)) {
                    updateCurrentController()
                    broadcastCurrentState()
                }
            }

            override fun onPlaybackStateChanged(state: PlaybackState?) {
                Log.d(TAG, "[$pkg] Playback state: ${state?.state}")
                updateCurrentController()
                broadcastCurrentState()
            }

            override fun onSessionDestroyed() {
                Log.d(TAG, "[$pkg] Session destroyed")
                unregisterCallback(pkg)
                updateCurrentController()
                broadcastCurrentState()
            }
        }

        controller.registerCallback(callback, handler)
        controllerCallbacks[pkg] = callback
        Log.d(TAG, "Registered callback for $pkg")
    }

    private fun unregisterCallback(packageName: String) {
        val controller = activeControllers.find { it.packageName == packageName }
        val callback = controllerCallbacks.remove(packageName)
        if (controller != null && callback != null) {
            controller.unregisterCallback(callback)
        }
    }

    private fun clearAllCallbacks() {
        for ((pkg, callback) in controllerCallbacks) {
            activeControllers.find { it.packageName == pkg }?.unregisterCallback(callback)
        }
        controllerCallbacks.clear()
    }

    /**
     * Pick the best controller: prefer actively playing, then most recently active.
     */
    private fun updateCurrentController() {
        // Prefer a controller that is actively playing
        val playing = activeControllers.firstOrNull { isPlaying(it) }
        val paused = activeControllers.firstOrNull { isPaused(it) }
        val newController = playing ?: paused ?: activeControllers.firstOrNull()

        if (newController != currentController) {
            Log.d(TAG, "Switching controller to: ${newController?.packageName}")
            currentController = newController
        }
    }

    private fun isPlaying(controller: MediaController): Boolean {
        return controller.playbackState?.state == PlaybackState.STATE_PLAYING
    }

    private fun isPaused(controller: MediaController): Boolean {
        return controller.playbackState?.state == PlaybackState.STATE_PAUSED
    }

    /**
     * Build a MediaState from the current controller and broadcast it.
     */
    fun broadcastCurrentState() {
        val state = buildMediaState()
        onMediaChanged?.invoke(state)
    }

    fun buildMediaState(): MediaState {
        val controller = currentController ?: return MediaState()
        val metadata = controller.metadata ?: return MediaState()
        val playbackState = controller.playbackState

        val title = metadata.getString(MediaMetadata.METADATA_KEY_TITLE) ?: ""
        val artist = metadata.getString(MediaMetadata.METADATA_KEY_ARTIST)
            ?: metadata.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST) ?: ""
        val album = metadata.getString(MediaMetadata.METADATA_KEY_ALBUM) ?: ""
        val duration = metadata.getLong(MediaMetadata.METADATA_KEY_DURATION)
        val rawPosition = playbackState?.position ?: 0L
        // Clamp position to duration - some apps report stale/invalid position on session connect
        val position = if (duration > 0L) rawPosition.coerceIn(0L, duration) else rawPosition
        val isPlaying = playbackState?.state == PlaybackState.STATE_PLAYING

        // Extract app name from package (e.g. "com.spotify.music" → "Spotify")
        val source = getAppName(controller.packageName)

        // Shuffle / repeat - read from PlaybackState extras (apps set these inconsistently,
        // so we default to safe values if not present)
        val extras = playbackState?.extras
        val shuffle = extras?.getBoolean("android.media.extra.SHUFFLE_MODE", false) ?: false
        val repeatMode = when (extras?.getInt("android.media.extra.REPEAT_MODE", 0) ?: 0) {
            1 -> RepeatMode.TRACK
            2 -> RepeatMode.LIST
            else -> RepeatMode.NONE
        }

        return MediaState(
            title = title,
            artist = artist,
            album = album,
            source = source,
            durationMs = duration,
            positionMs = position,
            isPlaying = isPlaying,
            shuffle = shuffle,
            repeatMode = repeatMode,
            packageName = controller.packageName
        )
    }

    /**
     * Get a human-readable app name from package name.
     */
    private fun getAppName(packageName: String): String {
        return try {
            val pm = applicationContext.packageManager
            val appInfo = pm.getApplicationInfo(packageName, 0)
            pm.getApplicationLabel(appInfo).toString()
        } catch (e: Exception) {
            // Fallback: derive from package name
            packageName.split(".").lastOrNull()
                ?.replaceFirstChar { it.uppercase() } ?: packageName
        }
    }

    /**
     * Get current playback position with interpolation.
     */
    fun getCurrentPositionMs(): Long {
        val controller = currentController ?: return 0L
        val playbackState = controller.playbackState ?: return 0L

        return if (playbackState.state == PlaybackState.STATE_PLAYING) {
            val elapsed = android.os.SystemClock.elapsedRealtime() - playbackState.lastPositionUpdateTime
            playbackState.position + (elapsed * playbackState.playbackSpeed).toLong()
        } else {
            playbackState.position
        }
    }

    /**
     * Media transport controls
     */
    fun play() = currentController?.transportControls?.play()
    fun pause() = currentController?.transportControls?.pause()
    fun skipNext() = currentController?.transportControls?.skipToNext()
    fun skipPrevious() = currentController?.transportControls?.skipToPrevious()
    fun stop() = currentController?.transportControls?.stop()
    fun seekTo(posMs: Long) = currentController?.transportControls?.seekTo(posMs)
}
