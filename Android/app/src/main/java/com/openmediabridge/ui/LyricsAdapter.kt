package com.openmediabridge.ui

import android.graphics.Color
import android.text.Html
import android.text.Spanned
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.RecyclerView
import com.openmediabridge.R
import com.openmediabridge.model.LyricsLine

/**
 * Lyrics RecyclerView adapter.
 * Mirrors the lyric-line states from full.html:
 *   - active: current line, full white, scale 1.0
 *   - sung: past lines, dimmed
 *   - upcoming: future lines, medium opacity
 */
class LyricsAdapter : RecyclerView.Adapter<LyricsAdapter.LyricViewHolder>() {

    private var lines: List<LyricsLine> = emptyList()
    private var activeIndex: Int = -1

    fun submitLines(newLines: List<LyricsLine>, newActiveIndex: Int) {
        val changed = newLines != lines || newActiveIndex != activeIndex
        if (!changed) return

        lines = newLines
        val oldActive = activeIndex
        activeIndex = newActiveIndex
        notifyDataSetChanged()
    }

    override fun getItemCount() = lines.size

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): LyricViewHolder {
        val view = LayoutInflater.from(parent.context)
            .inflate(R.layout.item_lyric_line, parent, false)
        return LyricViewHolder(view)
    }

    override fun onBindViewHolder(holder: LyricViewHolder, position: Int) {
        val line = lines[position]
        val state = when {
            position == activeIndex -> LineState.ACTIVE
            position < activeIndex -> LineState.SUNG
            else -> LineState.UPCOMING
        }
        holder.bind(line.text, state)
    }

    enum class LineState { ACTIVE, SUNG, UPCOMING }

    class LyricViewHolder(view: View) : RecyclerView.ViewHolder(view) {
        private val textView: TextView = view.findViewById(R.id.tv_lyric)

        fun bind(text: String, state: LineState) {
            // Handle word sync color tags
            val displayText: CharSequence = if (text.contains("<color=")) {
                parseColorTags(text)
            } else {
                text
            }

            textView.text = displayText

            when (state) {
                LineState.ACTIVE -> {
                    textView.alpha = 1.0f
                    textView.scaleX = 1.0f
                    textView.scaleY = 1.0f
                    textView.translationX = 0f
                    textView.setTextColor(Color.WHITE)
                }
                LineState.SUNG -> {
                    textView.alpha = 0.38f
                    textView.scaleX = 0.96f
                    textView.scaleY = 0.96f
                    textView.translationX = -8f
                    textView.setTextColor(Color.WHITE)
                }
                LineState.UPCOMING -> {
                    textView.alpha = 0.45f
                    textView.scaleX = 0.96f
                    textView.scaleY = 0.96f
                    textView.translationX = 0f
                    textView.setTextColor(Color.WHITE)
                }
            }
        }

        /**
         * Convert <color=yellow>word</color> tags to HTML spans.
         * Mirrors renderWordSync() in full.html.
         */
        private fun parseColorTags(text: String): Spanned {
            val html = text
                .replace("<color=yellow>", "<font color='#FDE68A'>")
                .replace("<color=white>", "<font color='#FFFFFF'>")
                .replace("</color>", "</font>")
            return Html.fromHtml(html, Html.FROM_HTML_MODE_COMPACT)
        }
    }
}
