package com.openmediabridge.websocket

import android.util.Log
import org.java_websocket.WebSocket
import org.java_websocket.handshake.ClientHandshake
import org.java_websocket.server.WebSocketServer as JWSServer
import java.net.InetSocketAddress
import java.nio.ByteBuffer

private const val TAG = "OMBWebSocket"

class OMBWebSocketServer(
    host: String,
    port: Int,
    private val onCommandReceived: (String) -> Unit
) : JWSServer(InetSocketAddress(host, port)) {

    val connectedCount get() = connections.size

    override fun onOpen(conn: WebSocket, handshake: ClientHandshake) {
        Log.d(TAG, "Client connected: ${conn.remoteSocketAddress} (port ${address.port})")
    }

    override fun onClose(conn: WebSocket, code: Int, reason: String, remote: Boolean) {
        Log.d(TAG, "Client disconnected: ${conn.remoteSocketAddress}")
    }

    override fun onMessage(conn: WebSocket, message: String) {
        Log.d(TAG, "Command: $message")
        onCommandReceived(message.trim())
    }

    override fun onMessage(conn: WebSocket, message: ByteBuffer) {}

    override fun onError(conn: WebSocket?, ex: Exception) {
        Log.e(TAG, "WebSocket error: ${ex.message}")
    }

    override fun onStart() {
        Log.d(TAG, "WebSocket server started on port ${address.port}")
        connectionLostTimeout = 100
    }

    fun broadcastLine(key: String, value: String) {
        broadcast("$key:$value")
    }
}

object CoverArtFetcher {
    private const val DEFAULT_COVER = "https://demo.tutorialzine.com/2015/03/html5-music-player/assets/img/default.png"

    suspend fun fetchCoverUrl(title: String, artist: String, album: String = ""): String {
        return fetchITunes(title, artist) ?: fetchDeezer(title, artist) ?: DEFAULT_COVER
    }

    private fun fetchITunes(title: String, artist: String): String? {
        return try {
            val query = java.net.URLEncoder.encode("$title $artist", "UTF-8")
            val url = "https://itunes.apple.com/search?term=$query&media=music&entity=song&limit=5"
            val conn = java.net.URL(url).openConnection() as java.net.HttpURLConnection
            conn.connectTimeout = 5000; conn.readTimeout = 5000
            conn.setRequestProperty("User-Agent", "OpenMediaBridge-Android")
            val root = org.json.JSONObject(conn.inputStream.bufferedReader().readText())
            val results = root.getJSONArray("results")
            for (i in 0 until results.length()) {
                val r = results.getJSONObject(i)
                val rArtist = r.optString("artistName", "")
                val rTrack = r.optString("trackName", "")
                if (rArtist.contains(artist, true) || artist.contains(rArtist, true) || rTrack.contains(title, true)) {
                    val art = r.optString("artworkUrl100", "")
                    if (art.isNotEmpty()) return art.replace("100x100", "600x600")
                }
            }
            null
        } catch (e: Exception) { null }
    }

    private fun fetchDeezer(title: String, artist: String): String? {
        return try {
            val query = java.net.URLEncoder.encode("$title $artist", "UTF-8")
            val url = "https://api.deezer.com/search?q=$query&limit=5"
            val conn = java.net.URL(url).openConnection() as java.net.HttpURLConnection
            conn.connectTimeout = 5000; conn.readTimeout = 5000
            val root = org.json.JSONObject(conn.inputStream.bufferedReader().readText())
            val data = root.getJSONArray("data")
            for (i in 0 until data.length()) {
                val r = data.getJSONObject(i)
                val rArtist = r.getJSONObject("artist").optString("name", "")
                val rTitle = r.optString("title", "")
                if (rArtist.contains(artist, true) || artist.contains(rArtist, true) || rTitle.contains(title, true)) {
                    val album = r.getJSONObject("album")
                    val cover = album.optString("cover_xl").ifEmpty { album.optString("cover_big", "") }
                    if (cover.isNotEmpty()) return cover
                }
            }
            null
        } catch (e: Exception) { null }
    }
}
