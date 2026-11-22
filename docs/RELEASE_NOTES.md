NodeX 1.29 fixes Android playback and startup auto-connect problems reported in 1.28 and lets you combine replay clips into one shareable WAV.

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

**Not yet verified:** nothing in 1.26 has been tried on a phone or in a car yet: the car-unplug release, the IPv6 network check, the background-service restart, the Spacebar release on Windows, the new RX noise threshold on the busy hubs and the jitter-buffer change are build- and unit-tested only. The car radio option and the mute change haven't been tried in a car; the radio option assumes the car tells the phone it has switched to the radio, and how it sounds depends on the car. The false-RX change hasn't been checked on a phone. The bubble chart link is build- and unit-tested only and hasn't been tapped on a phone yet. Private nodes are build- and unit-tested only. They haven't yet been dialled against a live private server, and share links, the phone screens and Android Auto haven't been tried on a device or in a car. IPv6 addresses are accepted but can't be dialled yet. macOS/Linux share-link registration is not built.
