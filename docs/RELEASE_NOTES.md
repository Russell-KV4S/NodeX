NodeX 1.18 removes the 15-second startup registration wait so your first node connects immediately.

**What changed**
- Connecting to your first node after starting NodeX now resolves and dials immediately instead of waiting up to 15 seconds for registration to settle. If a remote node has not picked up a brand-new registration yet, the existing automatic early-hangup retry handles it.

Includes all 1.17 changes (redial on unrequested far-end hang-up, per-network-change early-hangup retry reset, audio start/stop serialization, and Android Auto / media-button mute instead of hang-up).

Downloads:
- **Android:** `NodeX-android.apk` is the full sideload edition, including optional Android Auto Talk and steering-wheel PTT.
- **Windows:** extract the x64 or ARM64 ZIP and run `NodeX.UI.Desktop.exe`.
- **Linux:** extract the x64 or ARM64 tarball and run `./NodeX.UI.Desktop`. Install the distribution's PortAudio/JACK, font, X11 and secret-service dependencies as needed.
- **macOS:** extract the Apple Silicon or Intel ZIP and move `NodeX.app` to Applications. Ad-hoc signed, not notarized; macOS may need approval in Privacy & Security.
- **iOS:** excluded from this release pipeline.

**Private release:** repository access is required for downloads, and the in-app updater cannot reach a private feed.

**Switching from Google Play:** Play and sideload signatures differ. Export your settings before uninstalling the Play build; you may need to re-enter the node password.

**Not yet verified:** these fixes are unit-tested only; they have not been run on a phone, in a car or against a live node. Full Linux/macOS operation and Android update installation also still need hardware checks. See PROJECT_SPEC.md for the validation record.
