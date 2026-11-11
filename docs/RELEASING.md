# Releasing NodeX: two editions, one codebase

NodeX ships in two editions built from the **same branch** (`main`). There is no long-lived second branch to keep in sync; the difference is one build switch.

| | **Full** (GitHub Releases, sideload) | **Play Store** (Google Play) |
|---|---|---|
| Build property | none (default) | `-p:PlayStore=true` (defines `PLAY_STORE`) |
| Phone push-to-talk, Bluetooth PTT, headset media key | yes | yes |
| Android Auto: browse, listen, mute, switch nodes | yes | yes |
| Android Auto: **Talk button and steering-wheel talk** | yes (off until enabled in Settings) | **removed** (code path disabled, Settings options hidden) |
| Package format | `.apk` | `.aab` |

Why: Google restricts what Android Auto media apps may do and may reject a media app that transmits from the car. The Play edition keeps the car side listen-only so it can pass review. If Google's policy changes, flip the default and drop the switch.

How the switch works: `BuildFlavor.CarTalkAllowed` (`src/NodeX.Core/BuildFlavor.cs`) is a constant. `NodeSettings.CarPtt` and `CarPttWheel` read as off when it is `false`, so every car transmit check (`CarTransmit.Decide`, the Talk custom action, the wheel buttons) refuses, and the Settings card is hidden.

## Build commands

```bash
# Full edition, sideload APK (Release; signed if keystore/signing.props exists)
dotnet publish src/NodeX.UI/NodeX.UI.Android -c Release -p:AndroidPackageFormat=apk

# Play edition, AAB for Play Console
dotnet publish src/NodeX.UI/NodeX.UI.Android -c Release -p:PlayStore=true
```

Bump `ApplicationVersion` and `ApplicationDisplayVersion` in `NodeX.UI.Android.csproj` before either. Keep both editions on the same version number; Play needs the versionCode to rise on every upload.

Windows, Linux and macOS builds are always the full edition.

## Sideload and Play do not mix

Play re-signs the app with its own key, so a sideloaded APK signed with the upload key will **not** install over the Play version (and vice versa). Testers must uninstall one before installing the other. Say so in the release notes.

## GitHub Release checklist

1. All tests pass on `main`; CI is green.
2. Bump versions, build the full APK, and test it on a phone.
3. Tag `vX.Y` and create a GitHub Release with the APK attached, plus the Windows build (`tools/deploy-windows.ps1` shows how it is packaged).
4. Release notes: what changed, what is still untested (copy from `PROJECT_SPEC.md` §2), the licence reminder, and the uninstall-first note above.
5. For Play: build the Play edition, check the package with `jarsigner -verify`, and upload the `.aab` by hand.
6. Never commit `keystore/`.
