NodeX 1.33 shrinks the vertical gap around and between the **RX** and **TX** gain sliders inside the pinned PTT panel so more linked node cards remain visible while keeping full-size slider thumbs.

- **Tighter PTT panel height:** Removed excess built-in vertical slider margins (`SliderPreContentMargin` / `SliderPostContentMargin`) on the **RX** and **TX** controls and made the **Auto** toggle slimmer without shrinking the `20×20` touch thumb.

Automated builds, 867 Core tests, and 49 headless UI tests (with Skia layout checks) pass; physical Android phone verification remains pending.

**From 1.32**

- **Live RX & TX gain inside the PTT panel:** Shortened `PUSH TO TALK` / `Speaker` and moved the **RX** (`0.5×–4.0×` + **Auto** toggle) and **TX** (`0.5×–4.0×` microphone gain) sliders together directly inside the pinned PTT panel between `MIC`/`SPK` and `PTT`/`Speaker`, saving vertical scroll room for linked node cards (and collapsing onto one row in landscape).
- **Live TX mic gain updates:** Moving the **TX** slider applies immediately during active calls via `NodeAudioEngine.MicGain` / `NodeRuntime.SetLiveMicGain` without restarting capture and saves automatically alongside **RX** gain and **Auto**.

**From 1.31**

- **Receive gain (RX) & soft limiter:** Adjust receive gain from `0.5×` to `4.0×` right in the PTT panel or **Settings → Audio & PTT**. Changes apply immediately to all active links and **Audio Replay** playback with soft limiting, while saved Audio Replay WAV files remain clean at unity gain.
- **RX Auto-Level (optional):** Turn on **Auto** on the main screen (or **Auto-level incoming voices** in Settings) for gated AGC that lifts quiet stations and tames loud hubs without amplifying background hiss or squelch.
- **Android extra speaker boost (optional):** Enable **Extra speaker boost (Android LoudnessEnhancer)** in **Settings → Audio & PTT** (`+600` to `+1200 mB`) on supported Android devices.

**From 1.30**

- **IAX2 registration fix:** Fixed the Step 1 handshake with `register.allstarlink.org` (removed a premature `ACK` after `REGAUTH` and fixed sequence numbering), so IAX2 registration succeeds directly from the shared UDP call socket instead of timing out and falling back to HTTPS. Confirmed working live on AT&T cellular in v1.30 (`Registered (IAX2)`, `verdict=Consistent`, working live links).
- **Clearer diagnostics:** Reports numeric IAX2 rejection codes (such as `code 29`) and which step/server timed out.
- **Audio Replay:** Hardened saving the local replay index on Windows against brief file-lock interruptions.

**From 1.29**

- **Android playback:** NodeX now rebuilds stalled audio tracks and releases idle tracks so playback can recover without toggling Phone/Speaker.
- **Startup auto-connect on Android:** Fixed a background-thread error that could stop startup links from connecting.
- **Audio Replay:** Select several clips, optionally enter a title, and combine them in chronological order into one WAV. Your original clips are preserved.
- **Private nodes:** Saved hostnames stay out of the main screen, startup settings, and Android Auto browse. Edit the private node to see or change its host.
- **GitHub Android updates:** If links are active, choose **Disconnect & update** or **Cancel** in Settings. Leave Android Auto before installing an update.

Automated tests cover the changes. Physical phone retesting remains pending.

**From 1.28**

- **Audio Replay** saves received and transmitted clips on your device by default. Open the new replay button to hear the last received clip, browse, delete, or share clips. Replay plays through your local speaker and stops when you key PTT. You can turn clip saving off or change the clip limit in Settings.
- **Startup auto-connect** links to up to eight public or private nodes after the first successful registration each launch. Use the lightning bolt beside the favourite star to choose startup nodes. Your previous default node carries over.
- **Appearance** follows your system's light or dark mode by default. New choices include AMOLED black, Nord, Terminal green, and Sepia. Existing explicit theme selections are preserved.
- **Header controls** now use compact icon buttons for Exit and Audio Replay. The replay button indicates unheard clips.

The 1.28 changes passed automated tests. Physical phone and car checks remain pending.

**From 1.27**
- The MIC and SPK level meters now sit in the PTT and Speaker frame. GitHub Releases include the signed Play App Bundle and sideload APK.

**From 1.26**
- **Fewer false RX lights:** hubs that never announce their transmissions (53431, 60108, 43136 on a busy day) were lit by inaudible background noise. A node now has to be clearly louder than that noise to count as talking.
- **Transmit safety:** a node whose lookup or dial fails no longer quietly becomes your transmit node; unplugging Android Auto releases car push-to-talk and ends car mode; the key signal always goes out before any of your voice; a learned phone button can no longer have its release swallowed; Windows Spacebar push-to-talk lets go when you Alt+Tab away; a key that fails to send is no longer shown as "on air".
- **Connecting and reconnecting:** double-tapping Connect (or phone and car together) makes one call; Disconnect always removes the node; a network change no longer tears down a link that was set up during it; a node that rejects a reconnect is not retried forever; turning auto-reconnect off stops retries already running; a node that cannot be found yet is kept and retried until you disconnect it.
- **Android:** IPv6-only mobile data is no longer treated as offline; a crash when the audio route changes is fixed; the background service survives Android restarting it; Bluetooth call audio starts only when a headset is connected; tapping an active private node in the car works; a spoken favourite name dials that exact favourite; the car radio timer only runs when the option can apply.
- **Settings and privacy:** Save no longer switches IAX2 registration back on; a failed save restores the old password; the log hides JSON passwords and bearer tokens; registry error text never shows your password; the privacy notice now lists IAX2 registration and the STUN address checks.
- **Audio:** a very short blip (under 60 ms) no longer leaves that node's next transmission silent for up to half a minute.

**From 1.25**
- **Hear nodes over the car radio (new option, off by default):** Settings → "Android Auto: hear nodes over the car radio". When the car is playing its own radio, satellite or another app, a node is played the way navigation prompts are: the car turns the radio down while someone talks and back up about 1.5 s after they stop. Muted nodes stay silent. When NodeX itself is playing in the car, nothing changes.
- **Nothing is muted unless you press Mute:** car or Bluetooth Pause/Stop, navigation prompts, calls and other apps no longer mute your nodes, and Play no longer unmutes them. (Android Auto and some stereos send Pause on their own, which used to leave everything muted.) Node audio now keeps playing during a phone call in the car.
- **Fewer false "receiving" lights:** silent audio from an idle hub no longer lights it green, and NodeX remembers which hubs announce their transmissions across reconnects.
- **Private link cards** show the node number and name without the host name.

**From 1.24**
- **Bubble chart:** each public node now has a "Bubble chart ↗" link on its linked-node card, in Find a node results, and in a favourite's right-click / long-press menu. It opens the node's network map on stats.allstarlink.org in your browser. Private nodes don't have one, because they aren't on the stats site.

**From 1.23: private nodes**
- **Private nodes:** type `1999@host` or `1999@host:4570` in the Connect box, or paste a line from rpt.conf `[nodes]` (`radio@host:port/1999`). Save it and it appears with your favourites, marked PRIVATE. NodeX dials that address directly, looks the host name up fresh on every dial (so dynamic DNS works), and never asks allstarlink.org about it. It always logs in as `radio` with no password, which is how app_rpt links work.
- **Private Nodes screen:** add, edit, remove and test (IAX POKE) private nodes. A help card shows the exact `[nodes]` line the server's owner needs to add if their server doesn't load the public node list, with a copy button.
- **Share links:** `nodex://add?node=1999&host=...` links open NodeX on Android and Windows and ask before saving. They never connect on their own. On Windows, clicking a link while NodeX is open hands it to the running copy.
- **Android Auto:** private nodes show in the car's favourites, can be found by voice, and "play NodeX" resumes the last one you used.
- If a private server accepts the call and then hangs up straight away, NodeX now tells you the server probably doesn't know your node and points to the rpt.conf fix.

Includes all 1.22 changes (retry until you disconnect).

Downloads:
- **Android:** `NodeX-android.apk` is the full sideload edition, including optional Android Auto Talk and steering-wheel PTT.
- **Google Play:** `NodeX-android-play.aab` is the signed Play Console upload bundle. Use the APK above for direct installation.
- **Windows:** extract the x64 or ARM64 ZIP and run `NodeX.UI.Desktop.exe`.
- **Linux:** extract the x64 or ARM64 tarball and run `./NodeX.UI.Desktop`. Install the distribution's PortAudio/JACK, font, X11 and secret-service dependencies as needed.
- **macOS:** extract the Apple Silicon or Intel ZIP and move `NodeX.app` to Applications. Ad-hoc signed, not notarized; macOS may need approval in Privacy & Security.
- **iOS:** excluded from this release pipeline.

**Switching from Google Play:** Play and sideload signatures differ. Export your settings before uninstalling the Play build; you may need to re-enter the node password.

**Not yet verified:** in 1.33, 1.32, and 1.31, the compact PTT/Speaker layout, tighter live RX/TX gain sliders inside the PTT panel, RX Auto-Level, Audio Replay playback gain, and Android `LoudnessEnhancer` speaker boost have not yet been verified on a physical phone. Earlier items also pending real-device/car verification: car-unplug PTT release, IPv6 network check, background-service restart, Windows Spacebar release on Alt+Tab, hub noise threshold and jitter-buffer fix on a phone, car radio mixing and mute behaviour in a car, bubble chart links tapped on a phone, private-node host-hiding UI checks and `nodex://add?...` share links on Android/Windows, IPv6 dialing (accepted only), macOS/Linux share-link registration, Android 1.29 playback stall recovery, clip combining, and GitHub update disconnect prompt on a phone.
