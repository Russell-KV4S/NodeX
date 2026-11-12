# iOS release / TestFlight handover

**Status (2026-11-06): not started by the owner.** The owner has no Mac and chose not to pay for the Apple Developer Program ($99/year). The iOS head compiles and is prepared for release, but it has **never been built into an `.ipa` or run on a device**. This file is for anyone (a future contributor, or the owner later) who wants to pick it up.

## What is already done in the repo
- `src/NodeX.UI/NodeX.UI.iOS/Info.plist`: bundle ID `com.kv4s.nodex` (same as Android), display name `NodeX`, version 1.15 / build 17 (kept in step with Android), `arm64`, `ITSAppUsesNonExemptEncryption=false`, `XSAppIconAssets` pointing at the icon set, microphone and Bluetooth usage strings, background modes `audio` and `bluetooth-central`.
- `Resources/Assets.xcassets/AppIcon.appiconset`: 1024x1024 RGB icon (no alpha, which Apple requires) made from `branding/exports/nodex-app-icon-1024.png`. **Unproven:** the asset catalog is only compiled on a Mac, so the first Mac build may need fixes here.
- `dotnet build src/NodeX.UI/NodeX.UI.iOS` passes on Windows (compile check only).

## What is needed (none of it exists yet)
1. **Apple Developer Program** membership (paid, individual is fine). Free Apple IDs cannot use TestFlight.
2. **App Store Connect app record** with bundle ID `com.kv4s.nodex` (the owner may prefer a different ID, since Apple IDs are unique worldwide; if so change `CFBundleIdentifier` in `Info.plist`).
3. **A Mac with Xcode**, or a cloud Mac (MacinCloud, AWS EC2 Mac), or a **GitHub Actions `macos-latest` runner** (repo is `Russell-KV4S/NodeX`).
4. **Signing:** an Apple Distribution certificate (.p12) and an App Store provisioning profile for the bundle ID, or an App Store Connect API key (issuer ID, key ID, .p8) with automatic signing. Keep these as GitHub secrets, never in the repo.

## Build and upload (on the Mac / runner)
```bash
dotnet workload install ios
dotnet publish src/NodeX.UI/NodeX.UI.iOS -f net10.0-ios -c Release \
  -p:RuntimeIdentifier=ios-arm64 -p:ArchiveOnBuild=true \
  -p:CodesignKey="Apple Distribution: <name> (<team id>)" \
  -p:CodesignProvision="<profile name>"
# then upload the .ipa with Transporter, or:
xcrun altool --upload-app -f <app>.ipa -t ios --apiKey <key id> --apiIssuer <issuer id>
```
These exact property names are from the .NET iOS docs and are **unverified for this project**. Bump `CFBundleVersion` for every upload (Apple rejects a repeated build number). Apple processes the build for roughly 15 to 30 minutes, then it appears in TestFlight; internal testers (up to 100 App Store Connect users) need no Apple review.

A GitHub Actions workflow was planned (build, sign from secrets, upload to TestFlight) but **not written**, because it cannot be tested without an Apple account.

## Known iOS gaps (see PROJECT_SPEC.md sections 7 and 20)
- Audio (`AVAudioEngine`, `IosAudioSessionManager`) has never run on hardware; simultaneous engines for multi-node are an unverified assumption.
- Idle background registration is **not implemented** on iOS; Apple may suspend the app despite the zero-PCM keeper.
- `MicrophoneAllowed` reporting, CoreBluetooth transport and station-ID notifications are implemented. Their delivery, safe-area insets and real background behavior remain unverified on an iPhone.
- No Exit menu item on iOS.
