package io.github.ckenisell.resumerelay

import android.app.Activity
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.util.TypedValue
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URL
import java.net.URLEncoder
import java.util.UUID
import java.util.concurrent.Executors

/**
 * Resume Relay's screen app (Live Poster). It finds Resume Relay on the network, shows a 4-digit code until the screen
 * is added on the Live Poster tab, then asks every 2 seconds what to show and plays it: a looping video of the poster
 * that's playing, or a slideshow round (when a round ends it asks for the next).
 *
 * The video plays on a SurfaceView, which TVs and sticks put on their video layer: that's what shows at full 4K
 * (an app's own graphics are drawn at 1080p on most of them). The screen is kept on while the app is open.
 */
class MainActivity : Activity() {
    private val main = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private val prefs by lazy { getSharedPreferences("screen", Context.MODE_PRIVATE) }

    private lateinit var player: ExoPlayer
    private lateinit var playerView: PlayerView
    private lateinit var overlay: LinearLayout
    private lateinit var title: TextView
    private lateinit var code: TextView
    private lateinit var message: TextView
    private lateinit var address: EditText
    private lateinit var buttons: LinearLayout
    private lateinit var status: TextView

    private var server: String = ""          // http://10.0.0.30:8090
    private var token: String = ""
    private var screenName = ""
    private var version = ""                 // what's playing (the video's file name)
    private var looping = true
    private var running = false
    private var failures = 0
    private var showingMenu = false

    // ------------------------------------------------------------------ setup

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()

        player = ExoPlayer.Builder(this).build().apply {
            volume = 0f
            addListener(object : Player.Listener {
                override fun onPlaybackStateChanged(state: Int) {
                    if (state == Player.STATE_ENDED && !looping) roundEnded()
                }
                override fun onPlayerError(error: PlaybackException) {
                    status.text = "Can't play this video (${error.errorCodeName}). Trying again…"
                    status.visibility = View.VISIBLE
                    main.postDelayed({ val v = version; version = ""; if (v.isNotEmpty()) poll() }, 5000)
                }
                override fun onRenderedFirstFrame() { status.visibility = View.GONE }
            })
        }
        playerView = PlayerView(this).apply {
            useController = false
            resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT
            setShutterBackgroundColor(Color.TRANSPARENT)
            setKeepContentOnPlayerReset(true)
            setBackgroundColor(Color.BLACK)
            player = this@MainActivity.player
        }

        title = text(32f, Color.WHITE, bold = true)
        code = text(110f, Color.rgb(0xF0, 0xB4, 0x4C), bold = true).apply { letterSpacing = 0.15f }
        message = text(20f, Color.rgb(0xC8, 0xC4, 0xD8))
        address = EditText(this).apply {
            hint = "10.0.0.30:8090"
            setTextColor(Color.WHITE)
            setHintTextColor(Color.GRAY)
            textSize = 22f
            isSingleLine = true
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            imeOptions = EditorInfo.IME_ACTION_GO
            setOnEditorActionListener { _, _, _ -> connectTo(text.toString()); true }
            minWidth = dp(320)
        }
        buttons = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        overlay = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(dp(32), dp(32), dp(32), dp(32))
            setBackgroundColor(Color.rgb(0x15, 0x1A, 0x2E))
            addView(title); addView(code); addView(message); addView(address); addView(buttons)
        }
        status = text(14f, Color.rgb(0x9C, 0x99, 0xB5)).apply {
            setBackgroundColor(Color.argb(170, 0, 0, 0)); setPadding(dp(10), dp(4), dp(10), dp(4)); visibility = View.GONE
        }
        val root = FrameLayout(this).apply {
            setBackgroundColor(Color.BLACK)
            addView(playerView, FrameLayout.LayoutParams(-1, -1))
            addView(overlay, FrameLayout.LayoutParams(-1, -1))
            addView(status, FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM or Gravity.START).apply { setMargins(dp(12), 0, 0, dp(12)) })
        }
        setContentView(root)
        server = prefs.getString("server", "") ?: ""
        token = prefs.getString("token", "") ?: ""
    }

    override fun onResume() {
        super.onResume()
        hideSystemBars()
        running = true
        if (server.isEmpty()) discover() else hello()
    }

    override fun onPause() {
        super.onPause()
        running = false
        main.removeCallbacksAndMessages(null)
        player.pause()
    }

    override fun onDestroy() {
        super.onDestroy()
        player.release()
        io.shutdownNow()
    }

    private val deviceKey: String
        get() = prefs.getString("deviceKey", null) ?: UUID.randomUUID().toString().also { prefs.edit().putString("deviceKey", it).apply() }

    private val platform: String
        get() = when {
            packageManager.hasSystemFeature("amazon.hardware.fire_tv") -> "firetv"
            packageManager.hasSystemFeature(PackageManager.FEATURE_LEANBACK) -> "android-tv"
            else -> "android-phone"
        }

    // ------------------------------------------------------------------ finding Resume Relay

    /** Broadcasts "RESUMERELAY_DISCOVER" on port 47090; Resume Relay answers with its port. */
    private fun discover() {
        showSetup("Looking for Resume Relay…", "", "Make sure this device is on the same network as the computer running Resume Relay.")
        io.execute {
            var found: String? = null
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            val lock = try { wifi?.createMulticastLock("rr")?.apply { acquire() } } catch (e: Exception) { null }
            try {
                DatagramSocket().use { s ->
                    s.broadcast = true
                    s.soTimeout = 1500
                    val msg = "RESUMERELAY_DISCOVER".toByteArray()
                    val targets = mutableListOf(InetAddress.getByName("255.255.255.255"))
                    subnetBroadcast(wifi)?.let { targets.add(it) }
                    for (attempt in 0 until 3) {
                        for (t in targets) try { s.send(DatagramPacket(msg, msg.size, t, 47090)) } catch (e: Exception) { }
                        try {
                            val buf = ByteArray(1024)
                            val p = DatagramPacket(buf, buf.size)
                            s.receive(p)
                            val j = JSONObject(String(p.data, 0, p.length))
                            if (j.optString("app") == "Resume Relay") {
                                found = "http://${p.address.hostAddress}:${j.optInt("port", 8090)}"
                                break
                            }
                        } catch (e: Exception) { }
                    }
                }
            } catch (e: Exception) { }
            try { lock?.release() } catch (e: Exception) { }
            main.post {
                if (!running) return@post
                val f = found
                if (f != null) { server = f; prefs.edit().putString("server", f).apply(); hello() }
                else askAddress("Resume Relay wasn't found by itself.")
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun subnetBroadcast(wifi: WifiManager?): InetAddress? = try {
        val d = wifi?.dhcpInfo
        if (d == null || d.ipAddress == 0) null else {
            val b = (d.ipAddress and d.netmask) or d.netmask.inv()
            InetAddress.getByAddress(byteArrayOf((b and 0xff).toByte(), (b shr 8 and 0xff).toByte(), (b shr 16 and 0xff).toByte(), (b shr 24 and 0xff).toByte()))
        }
    } catch (e: Exception) { null }

    private fun askAddress(why: String) {
        showSetup("Resume Relay's address", "", "$why Type the address of the computer running it (the one you open Resume Relay at), for example 10.0.0.30:8090.")
        address.visibility = View.VISIBLE
        address.setText(server.removePrefix("http://"))
        setButtons("Connect" to { connectTo(address.text.toString()) }, "Search again" to { discover() })
        address.requestFocus()
    }

    private fun connectTo(input: String) {
        var a = input.trim().removePrefix("http://").removePrefix("https://").trimEnd('/')
        if (a.isEmpty()) return
        if (!a.contains(':')) a += ":8090"
        server = "http://$a"
        prefs.edit().putString("server", server).apply()
        hello()
    }

    // ------------------------------------------------------------------ pairing

    private fun hello() {
        if (!running) return
        io.execute {
            val body = JSONObject()
                .put("deviceKey", deviceKey).put("platform", platform)
                .put("model", "${Build.MANUFACTURER} ${Build.MODEL}".trim())
                .put("version", BuildConfigVersion.name(this))
            val r = request("POST", "/api/screen/hello", body.toString())
            main.post {
                if (!running) return@post
                if (r == null) {
                    failures++
                    if (failures >= 3) { failures = 0; askAddress("Resume Relay didn't answer at ${server.removePrefix("http://")}.") }
                    else { showSetup("Connecting…", "", "Trying ${server.removePrefix("http://")}…"); main.postDelayed({ hello() }, 3000) }
                    return@post
                }
                failures = 0
                if (r.optBoolean("paired")) {
                    token = r.optString("token"); screenName = r.optString("name")
                    prefs.edit().putString("token", token).apply()
                    hideSetup()
                    poll()
                } else {
                    showSetup("Add this screen", r.optString("code"),
                        "On a computer or phone, open Resume Relay at ${server.removePrefix("http://")}, go to the Live Poster tab, and enter this code.")
                    setButtons("Change address" to { askAddress("") })
                    main.postDelayed({ hello() }, 3000)
                }
            }
        }
    }

    // ------------------------------------------------------------------ playing

    private fun poll(ended: String = "") {
        if (!running || token.isEmpty()) return
        main.removeCallbacks(pollTask)
        io.execute {
            val q = "/api/screen/state?token=${enc(token)}" + if (ended.isNotEmpty()) "&ended=${enc(ended)}" else ""
            val r = request("GET", q, null)
            main.post {
                if (!running) return@post
                when {
                    r == null -> { status.text = "Resume Relay isn't answering…"; status.visibility = View.VISIBLE }
                    !r.optBoolean("paired", false) -> { token = ""; prefs.edit().remove("token").apply(); version = ""; player.stop(); hello(); return@post }
                    else -> {
                        if (status.text.startsWith("Resume Relay isn't")) status.visibility = View.GONE
                        val url = r.optString("url"); val v = r.optString("version")
                        if (url.isNotEmpty() && (v != version || (ended.isNotEmpty() && v == ended))) play(url, v, r.optBoolean("loop", true))
                    }
                }
                main.postDelayed(pollTask, 2000)
            }
        }
    }
    private val pollTask = Runnable { poll() }

    private fun play(url: String, v: String, loop: Boolean) {
        looping = loop
        player.repeatMode = if (loop) Player.REPEAT_MODE_ONE else Player.REPEAT_MODE_OFF
        if (v == version && player.mediaItemCount > 0) { player.seekTo(0); player.play(); return }
        version = v
        player.setMediaItem(MediaItem.fromUri(Uri.parse(server + url)))
        player.prepare()
        player.play()
    }

    /** A slideshow round finished: Resume Relay hands over the next one (or the same again). */
    private fun roundEnded() = poll(ended = version)

    // ------------------------------------------------------------------ the menu (OK on the remote, or a tap)

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action == KeyEvent.ACTION_UP && overlay.visibility != View.VISIBLE &&
            event.keyCode in listOf(KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_MENU, KeyEvent.KEYCODE_NUMPAD_ENTER)) {
            showMenu(); return true
        }
        if (event.action == KeyEvent.ACTION_UP && event.keyCode == KeyEvent.KEYCODE_BACK && showingMenu) { hideSetup(); return true }
        return super.dispatchKeyEvent(event)
    }

    override fun onUserInteraction() {
        super.onUserInteraction()
    }

    override fun onTouchEvent(event: android.view.MotionEvent): Boolean {
        if (event.action == android.view.MotionEvent.ACTION_UP && overlay.visibility != View.VISIBLE) { showMenu(); return true }
        return super.onTouchEvent(event)
    }

    private fun showMenu() {
        showSetup(screenName.ifEmpty { "Resume Relay" }, "", "Connected to Resume Relay at ${server.removePrefix("http://")}. Change what this screen shows on the Live Poster tab.")
        showingMenu = true
        setButtons(
            "Close" to { hideSetup() },
            "Change address" to { askAddress("") },
            "Unpair this screen" to {
                prefs.edit().remove("token").remove("deviceKey").apply()
                token = ""; version = ""; player.stop(); hello()
            })
    }

    // ------------------------------------------------------------------ helpers

    private fun showSetup(t: String, c: String, m: String) {
        overlay.visibility = View.VISIBLE
        title.text = t
        code.text = c; code.visibility = if (c.isEmpty()) View.GONE else View.VISIBLE
        message.text = m
        address.visibility = View.GONE
        buttons.removeAllViews()
        showingMenu = false
    }

    private fun hideSetup() {
        overlay.visibility = View.GONE
        showingMenu = false
        hideSystemBars()
    }

    private fun setButtons(vararg items: Pair<String, () -> Unit>) {
        buttons.removeAllViews()
        for ((label, action) in items) {
            buttons.addView(Button(this).apply {
                text = label
                isAllCaps = false
                textSize = 18f
                setTextColor(Color.WHITE)
                background = GradientDrawable().apply { cornerRadius = dp(10).toFloat(); setColor(Color.rgb(0x24, 0x2B, 0x4D)); setStroke(dp(2), Color.rgb(0x2F, 0x37, 0x60)) }
                setOnFocusChangeListener { v, f -> (v.background as GradientDrawable).setStroke(dp(2), if (f) Color.rgb(0xF0, 0xB4, 0x4C) else Color.rgb(0x2F, 0x37, 0x60)) }
                setPadding(dp(20), dp(10), dp(20), dp(10))
                setOnClickListener { action() }
            }, LinearLayout.LayoutParams(-2, -2).apply { setMargins(dp(8), dp(20), dp(8), 0) })
        }
        if (address.visibility != View.VISIBLE) buttons.getChildAt(0)?.requestFocus()
    }

    private fun text(size: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        setTextSize(TypedValue.COMPLEX_UNIT_SP, size)
        setTextColor(color)
        gravity = Gravity.CENTER
        if (bold) typeface = Typeface.DEFAULT_BOLD
        setPadding(0, dp(6), 0, dp(6))
        maxWidth = dp(900)
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8")

    @Suppress("DEPRECATION")
    private fun hideSystemBars() {
        window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN)
    }

    /** A JSON request to Resume Relay. Null when it doesn't answer; a 401 comes back as {"paired": false}. */
    private fun request(method: String, path: String, body: String?): JSONObject? = try {
        val c = URL(server + path).openConnection() as HttpURLConnection
        c.requestMethod = method
        c.connectTimeout = 4000
        c.readTimeout = 8000
        if (body != null) {
            c.doOutput = true
            c.setRequestProperty("Content-Type", "application/json")
            c.outputStream.use { it.write(body.toByteArray()) }
        }
        val code = c.responseCode
        val text = (if (code < 400) c.inputStream else c.errorStream)?.bufferedReader()?.use { it.readText() } ?: ""
        c.disconnect()
        if (code == 401) JSONObject().put("paired", false) else if (code in 200..299) JSONObject(text) else null
    } catch (e: Exception) { null }
}

/** The app's version name (from the release tag), without needing BuildConfig. */
object BuildConfigVersion {
    fun name(ctx: Context): String = try {
        ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName ?: ""
    } catch (e: Exception) { "" }
}
