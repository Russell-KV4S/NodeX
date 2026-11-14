NodeX 1.20 adds an optional tip jar.

**What changed**
- Settings has a new "Support NodeX" card with a "Buy me a coffee" button that opens buymeacoffee.com/RussellThomas in your browser. Tipping is optional and doesn't unlock or change anything in the app.

Includes all 1.19 changes (callsign field and "Listen only" option removed).

Downloads:
- **Android:** `NodeX-android.apk` is the full sideload edition, including optional Android Auto Talk and steering-wheel PTT.
- **Windows:** extract the x64 or ARM64 ZIP and run `NodeX.UI.Desktop.exe`.
- **Linux:** extract the x64 or ARM64 tarball and run `./NodeX.UI.Desktop`. Install the distribution's PortAudio/JACK, font, X11 and secret-service dependencies as needed.
- **macOS:** extract the Apple Silicon or Intel ZIP and move `NodeX.app` to Applications. Ad-hoc signed, not notarized; macOS may need approval in Privacy & Security.
- **iOS:** excluded from this release pipeline.

**Switching from Google Play:** Play and sideload signatures differ. Export your settings before uninstalling the Play build; you may need to re-enter the node password.

**Not yet verified:** these changes are build- and unit-tested only; they have not been run on a phone, in a car or against a live node. Full Linux/macOS operation and Android update installation also still need hardware checks. See PROJECT_SPEC.md for the validation record.
