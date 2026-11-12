NodeX 1.16 fixes the Android Auto launcher icon and adds the first GitHub updater for the full Android edition.

Downloads:
- **Android:** `NodeX-android.apk` is the full sideload edition, including optional Android Auto Talk and steering-wheel PTT. Enable Talk from the car and choose the wheel button in Settings.
- **Windows:** extract the x64 or ARM64 ZIP and run `NodeX.UI.Desktop.exe`.
- **Linux:** extract the x64 or ARM64 tarball and run `./NodeX.UI.Desktop`. Install the distribution's PortAudio/JACK, font, X11 and secret-service dependencies as needed. Desktop Bluetooth may require BlueZ/D-Bus permissions.
- **macOS:** extract the Apple Silicon or Intel ZIP and move `NodeX.app` to Applications. These builds are ad-hoc signed, not Apple Developer ID signed or notarized; macOS may require explicit approval in Privacy & Security. The app targets macOS 12+, but bundled Apple Silicon Bluetooth needs macOS 15+; Intel macOS has no bundled Bluetooth backend.
- **iOS:** excluded from this release pipeline.

Android updates check GitHub on opening (at most daily), offer a manual check in Settings, verify downloaded size/SHA-256 and the installed signing key, then ask Android to confirm installation. Downloads and installs require a deliberate tap. Disconnect nodes and leave Android Auto before installing. Settings/passwords survive an update signed with the same key.

**Private release:** repository access is currently required for downloads. The app does not contain GitHub credentials, so its update feed will report unavailable until public access is enabled. Download privately through GitHub in the meantime.

**Switching from Google Play:** Play and sideload signatures can differ. Export your settings before uninstalling the Play build to switch editions; you may need to re-enter the node password. In-place sideload updates require the same signing key and an increased Android versionCode.

These packages pass automated tests/builds. Full Linux/macOS operation, Android update installation and the car icon still need verification on hardware. See PROJECT_SPEC.md for the platform validation record. Desktop packages retain the SimpleBLE license and third-party notices; its noncommercial/commercial terms are separate from NodeX's MIT license.
