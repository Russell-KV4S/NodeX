NodeX 1.17 fixes linked nodes disappearing and makes media commands in the car mute instead of hang up.

**What changed**
- A linked node whose far end hangs up an established call is now redialled (up to 5 times in a row) unless the far node asked to disconnect. Before, it was silently dropped.
- A network change (Wi-Fi to cellular and back) now gives every node a fresh set of "node doesn't know this address yet" retries, so a phone that keeps hopping no longer loses nodes.
- One hang-up can no longer be counted as two retries.
- Fixed "Audio could not start: Object reference not set to an instance of an object" on a call that is hung up right after it connects.
- Android Auto / media buttons: Pause and Stop (car screen, steering wheel, headset, Bluetooth stereo), and another app taking the audio, now mute the linked nodes and keep them linked. Play unmutes. Use the per-node Disconnect to hang up.
- The troubleshooting log now says why any node leaves the list. If one vanishes, send the whole log from Settings, Send logs.

Downloads:
- **Android:** `NodeX-android.apk` is the full sideload edition, including optional Android Auto Talk and steering-wheel PTT.
- **Windows:** extract the x64 or ARM64 ZIP and run `NodeX.UI.Desktop.exe`.
- **Linux:** extract the x64 or ARM64 tarball and run `./NodeX.UI.Desktop`. Install the distribution's PortAudio/JACK, font, X11 and secret-service dependencies as needed.
- **macOS:** extract the Apple Silicon or Intel ZIP and move `NodeX.app` to Applications. Ad-hoc signed, not notarized; macOS may need approval in Privacy & Security.
- **iOS:** excluded from this release pipeline.

**Private release:** repository access is required for downloads, and the in-app updater cannot reach a private feed.

**Switching from Google Play:** Play and sideload signatures differ. Export your settings before uninstalling the Play build; you may need to re-enter the node password.

**Not yet verified:** these fixes are unit-tested only; they have not been run on a phone, in a car or against a live node. Full Linux/macOS operation and Android update installation also still need hardware checks. See PROJECT_SPEC.md for the validation record.
