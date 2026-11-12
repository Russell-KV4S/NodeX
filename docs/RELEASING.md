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

Bump `NodeXVersion` and `NodeXVersionCode` in `Directory.Build.props` before either. Android and desktop consume these values; Play needs the versionCode to rise on every upload. Update `docs/RELEASE_NOTES.md` for each release. iOS versioning remains separate while its release is on hold.

Windows, Linux and macOS builds are always the full edition.

## Sideload and Play do not mix

Play re-signs the app with its own key, so a sideloaded APK signed with the upload key will **not** install over the Play version (and vice versa). Testers must uninstall one before installing the other. Say so in the release notes.

## Automated GitHub releases (all platforms except iOS)

`.github/workflows/release.yml` builds and publishes on a version tag, or can be re-run manually for an existing tag. Releases inherit this repository's visibility: **private for now, by owner decision**. Do not change visibility as part of a release.

Configure these Actions secrets once, using the existing sideload/upload keystore (never a fresh key per build):

| Secret | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | Base64 of the keystore file |
| `ANDROID_KEYSTORE_PASSWORD` | Store password |
| `ANDROID_KEY_PASSWORD` | Key password |
| `ANDROID_KEY_ALIAS` | Existing alias |

The workflow gates publishing on Core/UI tests and all seven packages: Windows x64/ARM64 ZIPs, Linux x64/ARM64 tarballs, macOS Apple Silicon/Intel app-bundle ZIPs, and one signed universal **full-edition** Android APK (`PlayStore=false`). Self-contained desktop packages do not need a separate .NET install. macOS packages are ad-hoc signed only; Apple signing/notarization and real hardware checks remain pending. Intel Mac BLE is unsupported by the bundled native dependency. Linux system audio/windowing/keyring libraries still need to be installed.

`tools/package-release.ps1` packages each desktop RID on its native OS. `tools/write-update-manifest.ps1` generates `nodex-update.json` (schema 1, release version, Android package ID/versionCode/asset name/size/SHA-256) plus `SHA256SUMS.txt`. Stable asset names are defined in PROJECT_SPEC.md §3.7. Publish starts with a draft and only exposes a complete release after upload. Re-running may finish a draft; it refuses to replace an already published release.

After all local checks and review, commit/push the source, then create and push its matching version tag. Example for this release:

```bash
git tag v1.16
git push origin v1.16
gh run list --workflow release.yml
```

Keep monitoring the Actions run until completion. An uploaded artifact is not a published release. A failure leaves no new published release; fix/retry the same tag only if still unpublished. Never change the contents of a published version: increment the display version and Android code instead.

### Android sideload updates

The full Android edition checks the latest stable GitHub release on foreground resume (at most daily per process), provides a manual Settings check, and offers a user-triggered verified download/install. Android's installer requires confirmation; this is not silent installation. Disconnect all nodes and leave Android Auto before installing. The app verifies file size/SHA-256, package ID, increased versionCode and the same signing key; settings/passwords survive normal in-place updates. Keep signing-key backups outside GitHub as well as the Actions secrets.

**Public access is pending.** No GitHub token is shipped in the app. While the repository is private, the updater reports that the feed is unavailable; authorized testers must download through GitHub themselves. Publishing private releases does not enable anonymous in-app downloads. The Play edition omits this updater and its installation permission.

## Release validation checklist

1. All tests pass on `main`; CI is green.
   Run both `tests/NodeX.Core.Tests` and `tests/NodeX.UI.Tests`; the latter checks the UI dispatcher and renders themes/enlarged text. Confirm the actual hosted CI result separately.
2. Bump the shared versions, update release notes, and test the full APK on a phone where possible.
3. Tag `vX.Y` and follow the Release workflow through publication of all platforms except iOS.
4. Release notes: what changed, what is still untested (copy from `PROJECT_SPEC.md` §2), the licence reminder, and the uninstall-first note above.
5. For Play: build the Play edition, check the package with `jarsigner -verify`, and upload the `.aab` by hand.
6. Never commit `keystore/`.

Desktop packages must retain `licenses/SimpleBLE-LICENSE.md` and the separate dependency notice from `THIRD_PARTY_NOTICES.md`. macOS bundle scaffolding is in `tools/package-macos.sh`; it still needs a Mac smoke test, signing and notarization. The bundled desktop BLE library supports macOS 15+ on Apple Silicon, not Intel Macs.
