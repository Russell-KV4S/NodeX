NodeX 1.19 removes the callsign field and the "Listen only" option.

**What changed**
- The callsign setting is gone. It was only ever shown in the header and log, never sent to AllStarLink or any node. Settings now asks for just your AllStarLink node number and node password; the header shows the node number. Transmitting needs an amateur radio licence, and NodeX does not ask for or check your callsign.
- "Listen only" is gone. Nothing in NodeX works without an AllStarLink node number and password, so the option was misleading. Listening still works when the microphone permission is denied; you just cannot transmit.
- Old `Callsign` / `ListenOnly` entries in an existing settings file are ignored.

Includes all 1.18 changes (immediate first-node connect).

Downloads:
- **Android:** `NodeX-android.apk` is the full sideload edition, including optional Android Auto Talk and steering-wheel PTT.
- **Windows:** extract the x64 or ARM64 ZIP and run `NodeX.UI.Desktop.exe`.
- **Linux:** extract the x64 or ARM64 tarball and run `./NodeX.UI.Desktop`. Install the distribution's PortAudio/JACK, font, X11 and secret-service dependencies as needed.
- **macOS:** extract the Apple Silicon or Intel ZIP and move `NodeX.app` to Applications. Ad-hoc signed, not notarized; macOS may need approval in Privacy & Security.
- **iOS:** excluded from this release pipeline.

**Switching from Google Play:** Play and sideload signatures differ. Export your settings before uninstalling the Play build; you may need to re-enter the node password.

**Not yet verified:** these changes are unit-tested only; they have not been run on a phone, in a car or against a live node. Full Linux/macOS operation and Android update installation also still need hardware checks. See PROJECT_SPEC.md for the validation record.
