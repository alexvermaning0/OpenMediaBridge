package com.openmediabridge.ui

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.graphics.Bitmap
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.os.IBinder
import android.view.View
import android.view.WindowManager
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import androidx.palette.graphics.Palette
import androidx.recyclerview.widget.LinearLayoutManager
import com.bumptech.glide.Glide
import com.bumptech.glide.load.resource.bitmap.RoundedCorners
import com.bumptech.glide.request.target.CustomTarget
import com.bumptech.glide.request.transition.Transition
import com.openmediabridge.databinding.ActivityMainBinding
import com.openmediabridge.model.LyricsState
import com.openmediabridge.model.MediaState
import com.openmediabridge.service.BridgeService
import com.openmediabridge.service.MediaListenerService
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private var bridgeService: BridgeService? = null
    private var serviceBound = false
    private lateinit var lyricsAdapter: LyricsAdapter
    private var progressJob: Job? = null

    private val serviceConnection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, service: IBinder) {
            val binder = service as BridgeService.LocalBinder
            bridgeService = binder.getService()
            serviceBound = true

            // Hook callbacks
            bridgeService?.onMediaStateChanged = { state ->
                runOnUiThread { updateMediaUI(state) }
            }
            bridgeService?.onLyricsStateChanged = { state ->
                runOnUiThread { updateLyricsUI(state) }
            }
            bridgeService?.onLogMessage = { msg ->
                runOnUiThread { appendLog(msg) }
            }
            bridgeService?.onServerStatus = { status ->
                runOnUiThread { binding.tvServerStatus.text = status }
            }

            // Show initial state
            updateMediaUI(bridgeService?.currentMediaState ?: MediaState())
            updateLyricsUI(bridgeService?.currentLyricsState ?: LyricsState())
            updateServerInfo()
            updateToggleButtonStates()
        }

        override fun onServiceDisconnected(name: ComponentName) {
            serviceBound = false
            bridgeService = null
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Check permission first
        if (!SetupActivity.isNotificationAccessGranted(this)) {
            startActivity(Intent(this, SetupActivity::class.java))
            finish()
            return
        }

        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        // Keep screen on while app is visible
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        setupLyricsRecycler()
        setupControls()
        startBridgeService()
        bindBridgeService()
        startProgressTimer()
    }

    override fun onDestroy() {
        super.onDestroy()
        progressJob?.cancel()
        if (serviceBound) {
            unbindService(serviceConnection)
            serviceBound = false
        }
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private fun setupLyricsRecycler() {
        lyricsAdapter = LyricsAdapter()
        binding.rvLyrics.apply {
            layoutManager = LinearLayoutManager(this@MainActivity)
            adapter = lyricsAdapter
            itemAnimator = null // disable default animations, we handle them
        }
    }

    private fun setupControls() {
        // Media transport controls
        binding.btnPrevious.setOnClickListener {
            MediaListenerService.instance?.skipPrevious()
        }
        binding.btnPlayPause.setOnClickListener {
            val svc = MediaListenerService.instance ?: return@setOnClickListener
            if (bridgeService?.currentMediaState?.isPlaying == true) svc.pause() else svc.play()
        }
        binding.btnNext.setOnClickListener {
            MediaListenerService.instance?.skipNext()
        }
        binding.btnWordSync.setOnClickListener {
            bridgeService?.toggleWordSync()
            updateToggleButtonStates()
        }
        binding.btnNextLyrics.setOnClickListener {
            bridgeService?.nextLyrics()
        }
        binding.btnRefresh.setOnClickListener {
            bridgeService?.refreshLyrics()
        }
        binding.btnServerInfo.setOnClickListener {
            toggleServerPanel()
        }
        binding.btnOffline.setOnClickListener {
            bridgeService?.toggleOfflineMode()
            updateToggleButtonStates()
        }
        binding.btnCjk.setOnClickListener {
            bridgeService?.toggleCjkFilter()
            updateToggleButtonStates()
        }
        binding.btnPlain.setOnClickListener {
            bridgeService?.togglePlainFallback()
            updateToggleButtonStates()
        }
        binding.btnTranslate.setOnClickListener {
            bridgeService?.toggleTranslation()
            updateToggleButtonStates()
        }
        binding.btnLang.setOnClickListener {
            val service = bridgeService ?: return@setOnClickListener
            val langs = BridgeService.TRANSLATION_LANGUAGES
            val currentIdx = langs.indexOf(service.getTranslationTargetLang()).coerceAtLeast(0)
            val nextLang = langs[(currentIdx + 1) % langs.size]
            service.selectTranslationLanguage(nextLang)
            updateToggleButtonStates()
        }
        binding.btnClearCache.setOnClickListener {
            bridgeService?.clearCache()
        }
        binding.btnOffsetMinus.setOnClickListener {
            bridgeService?.decreaseOffset()
            updateOffsetDisplay()
        }
        binding.btnOffsetPlus.setOnClickListener {
            bridgeService?.increaseOffset()
            updateOffsetDisplay()
        }
    }

    private fun updateToggleButtonStates() {
        val service = bridgeService ?: return
        binding.btnWordSync.alpha = if (service.isWordSyncEnabled()) 1f else 0.4f
        binding.btnOffline.alpha = if (service.isOfflineModeEnabled()) 1f else 0.4f
        binding.btnCjk.alpha = if (service.isCjkFilterEnabled()) 1f else 0.4f
        binding.btnPlain.alpha = if (service.isPlainFallbackEnabled()) 1f else 0.4f
        binding.btnLang.text = service.getTranslationTargetLang().uppercase()
        updateOffsetDisplay()
        updateTranslateButtonColor()
    }

    private fun updateOffsetDisplay() {
        val service = bridgeService ?: return
        binding.tvOffset.text = "${service.getCurrentOffset()} ms"
    }

    // ── Service ───────────────────────────────────────────────────────────────

    private fun startBridgeService() {
        val intent = Intent(this, BridgeService::class.java)
        startForegroundService(intent)
    }

    private fun bindBridgeService() {
        val intent = Intent(this, BridgeService::class.java)
        bindService(intent, serviceConnection, Context.BIND_AUTO_CREATE)
    }

    // ── UI Updates ────────────────────────────────────────────────────────────

    private fun updateMediaUI(state: MediaState) {
        binding.tvTitle.text = state.title.ifEmpty { "—" }
        binding.tvSubtitle.text = buildList {
            if (state.source.isNotEmpty()) add(state.source)
            if (state.artist.isNotEmpty()) add(state.artist)
        }.joinToString(" · ").ifEmpty { "—" }

        binding.tvDuration.text = formatMs(state.durationMs)

        // Update notification bar
        updateNotificationBarTitle("${state.title} - ${state.artist}".trim())

        // Update play/pause button icon
        binding.btnPlayPause.setImageResource(
            if (state.isPlaying) android.R.drawable.ic_media_pause
            else android.R.drawable.ic_media_play
        )

        // Load album art
        if (state.coverUrl.isNotEmpty()) {
            loadAlbumArt(state.coverUrl)
        }
    }

    private fun updateLyricsUI(state: LyricsState) {
        val hasLyrics = state.hasLyrics
        binding.rvLyrics.visibility = if (hasLyrics) View.VISIBLE else View.GONE
        binding.tvNoLyrics.visibility = if (!hasLyrics) View.VISIBLE else View.GONE

        if (hasLyrics) {
            lyricsAdapter.submitLines(state.lines, state.currentLineIndex)
            scrollToActiveLine(state.currentLineIndex)
        }

        binding.tvLyricsSource.text = state.source
        binding.btnWordSync.alpha = if (state.wordSyncEnabled) 1f else 0.4f
    }

    private fun scrollToActiveLine(index: Int) {
        if (index < 0) return
        val llm = binding.rvLyrics.layoutManager as LinearLayoutManager
        // Center the active line
        val offset = binding.rvLyrics.height / 2 - 80 // approx line height offset
        llm.scrollToPositionWithOffset(index, offset)
    }

    private fun loadAlbumArt(url: String) {
        Glide.with(this)
            .asBitmap()
            .load(url)
            .transform(RoundedCorners(32))
            .into(object : CustomTarget<Bitmap>() {
                override fun onResourceReady(resource: Bitmap, transition: Transition<in Bitmap>?) {
                    binding.ivAlbumArt.setImageBitmap(resource)
                    binding.ivAlbumArtShadow.setImageBitmap(resource)
                    applyPaletteColors(resource)
                }
                override fun onLoadCleared(placeholder: android.graphics.drawable.Drawable?) {}
            })

        // Also load blurred background
        Glide.with(this)
            .load(url)
            .into(binding.ivBackground)
    }

    private fun applyPaletteColors(bitmap: Bitmap) {
        Palette.from(bitmap).generate { palette ->
            val dominant = palette?.getDominantColor(0xFF1a1a2e.toInt()) ?: 0xFF1a1a2e.toInt()
            val vibrant = palette?.getVibrantColor(dominant) ?: dominant

            // Apply gradient background tint
            val gradient = GradientDrawable(
                GradientDrawable.Orientation.TOP_BOTTOM,
                intArrayOf(
                    adjustAlpha(dominant, 0.6f),
                    adjustAlpha(dominant, 0.9f)
                )
            )
            binding.colorOverlay.background = gradient
        }
    }

    private fun adjustAlpha(color: Int, alpha: Float): Int {
        val a = (alpha * 255).toInt()
        return (a shl 24) or (color and 0x00FFFFFF)
    }

    // ── Progress Timer ────────────────────────────────────────────────────────

    private fun startProgressTimer() {
        progressJob = lifecycleScope.launch {
            while (isActive) {
                val state = bridgeService?.currentMediaState
                if (state != null && state.durationMs > 0) {
                    val pos = MediaListenerService.instance?.getCurrentPositionMs() ?: state.positionMs
                    val offset = bridgeService?.getCurrentOffset() ?: 0
                    val adjustedPos = pos + offset
                    val progress = (adjustedPos.toFloat() / state.durationMs).coerceIn(0f, 1f)
                    binding.progressBar.progress = (progress * 1000).toInt()
                    binding.tvPosition.text = formatMs(adjustedPos)
                }
                updateTranslateButtonColor()
                delay(500)
            }
        }
    }

    // Mirrors the console TUI's yellow=pending / green=on / red=off coloring for translation.
    private fun updateTranslateButtonColor() {
        val service = bridgeService ?: return
        val (color, alpha) = when {
            service.isTranslationPending() -> "#FFC107" to 1f   // yellow - fetching
            service.isTranslationEnabled() -> "#4CAF50" to 1f   // green - on and ready
            else -> "#40FFFFFF" to 0.4f                          // default - off
        }
        val csl = android.content.res.ColorStateList.valueOf(android.graphics.Color.parseColor(color))
        binding.btnTranslate.strokeColor = csl
        binding.btnTranslate.setTextColor(if (alpha == 1f && color != "#40FFFFFF") android.graphics.Color.parseColor(color) else android.graphics.Color.WHITE)
        binding.btnTranslate.alpha = alpha
    }

    // ── Log ───────────────────────────────────────────────────────────────────

    private val logBuffer = ArrayDeque<String>(12)

    private fun appendLog(msg: String) {
        logBuffer.addLast(msg)
        if (logBuffer.size > 12) logBuffer.removeFirst()
        binding.tvDebugLog.text = logBuffer.joinToString("\n")
    }

    // ── Server Panel ──────────────────────────────────────────────────────────

    private var serverPanelVisible = false

    private fun toggleServerPanel() {
        serverPanelVisible = !serverPanelVisible
        binding.cardServerInfo.visibility = if (serverPanelVisible) View.VISIBLE else View.GONE
        if (serverPanelVisible) updateServerInfo()
    }

    private fun updateServerInfo() {
        val service = bridgeService ?: return
        binding.tvMediaServer.text = "Media WS: ${service.getServerAddress()}"
        binding.tvLyricsServer.text = "Lyrics WS: ${service.getLyricsServerAddress()}"
        binding.tvClientCount.text = "Clients: ${service.getMediaClientCount()} media, ${service.getLyricsClientCount()} lyrics"
    }

    private fun updateNotificationBarTitle(text: String) {
        // Just update the status text at top of screen
        binding.tvServerStatus.text = text.ifEmpty { bridgeService?.getServerAddress() ?: "" }
    }

    private fun formatMs(ms: Long): String {
        val totalSec = ms / 1000
        val min = totalSec / 60
        val sec = totalSec % 60
        return "$min:${sec.toString().padStart(2, '0')}"
    }

    private fun ArrayDeque<String>.addLast(element: String) = add(element)
}
