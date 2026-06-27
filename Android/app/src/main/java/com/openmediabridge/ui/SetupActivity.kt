package com.openmediabridge.ui

import android.content.ComponentName
import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import android.text.TextUtils
import androidx.appcompat.app.AppCompatActivity
import com.openmediabridge.R
import com.openmediabridge.databinding.ActivitySetupBinding
import com.openmediabridge.service.MediaListenerService

/**
 * One-time setup screen. Guides the user to grant Notification Access,
 * which is required for MediaListenerService to read media sessions.
 */
class SetupActivity : AppCompatActivity() {

    private lateinit var binding: ActivitySetupBinding

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivitySetupBinding.inflate(layoutInflater)
        setContentView(binding.root)

        binding.btnGrantAccess.setOnClickListener {
            openNotificationSettings()
        }

        binding.btnContinue.setOnClickListener {
            if (isNotificationAccessGranted()) {
                goToMain()
            } else {
                binding.tvStatus.text = "⚠️ Permission not granted yet.\nPlease enable it in the settings."
                binding.tvStatus.setTextColor(getColor(android.R.color.holo_orange_light))
            }
        }
    }

    override fun onResume() {
        super.onResume()
        updatePermissionStatus()
    }

    private fun updatePermissionStatus() {
        if (isNotificationAccessGranted()) {
            binding.tvStatus.text = "✅ Notification access granted!"
            binding.tvStatus.setTextColor(getColor(android.R.color.holo_green_light))
            binding.btnContinue.isEnabled = true
            binding.btnGrantAccess.text = "Already granted ✓"
        } else {
            binding.tvStatus.text = "❌ Notification access not granted"
            binding.tvStatus.setTextColor(getColor(android.R.color.holo_red_light))
            binding.btnContinue.isEnabled = false
        }
    }

    private fun isNotificationAccessGranted(): Boolean {
        val flat = Settings.Secure.getString(contentResolver, "enabled_notification_listeners") ?: ""
        val cn = ComponentName(this, MediaListenerService::class.java)
        return flat.split(":").any { it.trim() == cn.flattenToString() }
    }

    private fun openNotificationSettings() {
        startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
    }

    private fun goToMain() {
        startActivity(Intent(this, MainActivity::class.java))
        finish()
    }

    companion object {
        fun isNotificationAccessGranted(context: android.content.Context): Boolean {
            val flat = Settings.Secure.getString(
                context.contentResolver, "enabled_notification_listeners") ?: ""
            val cn = ComponentName(context, MediaListenerService::class.java)
            return flat.split(":").any { it.trim() == cn.flattenToString() }
        }
    }
}
