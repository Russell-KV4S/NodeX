# NodeX — Project Specification & Engineering Guide
### Cross-platform, Asterisk-free AllStarLink node (.NET 10 / Avalonia UI)

> **Last updated:** 2026-11-12 · **Tests:** 692 Core/service tests + 18 headless UI tests. Both Android editions build. v1.16 published privately; **v1.17 (Android code 19)** adds the "nodes randomly dropping" fixes (§4.6, §5.6) and mute-instead-of-hang-up for car media commands, unit-tested only. Package/hardware validation distinctions are recorded in `CHANGELOG.md`.
>
> This document is written for **humans and AI coding agents** who pick the project up cold. Read §0 first.

---

## 0. How to use this document (read first)

1. **Authority order when sources disagree:** (1) the code and its tests, (2) this document, (3) anything else. An earlier "master specification" file existed and contained **wrong protocol details** (wrong registration endpoint, wrong IAX subclass numbers, wrong keep-alive); it has been removed from the folder and must not be recreated or trusted. Protocol facts below are verified against RFC 5456 and the ASL3 source — see §5 and §15.
2. **Be honest about verification.** The core protocol path has been validated live against a real ASL3 node (Windows only; log in `docs/LIVE_VALIDATION.md`), and the owner has field-tested Android screen-off audio, Wi-Fi/cellular handoff, voice quality and the Desktop Head Unit. Full Linux/macOS app and iPhone validation remain outstanding. §2 and §14 list exactly what is and is not verified. Do not describe unverified behaviour as working.
3. **Before changing behaviour, find its tests** (§11). Almost every rule in this document is pinned by a test. If you change a rule, change the test and this document together.
4. **Build rules matter** (§3.3): the repo is inside OneDrive and build output is deliberately redirected outside it. Do not undo `Directory.Build.props`.
5. **Conventions and pitfalls** (§12) record mistakes already made once. Skim them before writing code.

---

## 1. Project overview & objective

NodeX is a standalone AllStarLink (ASL) node for **Windows, Linux, macOS, Android and iOS**, built with **.NET 10** and **Avalonia UI**. It is a genuine peer node on the ASL network (IAX2 over UDP 4569, HTTPS registration) — **not** a WebTransceiver or `iaxrpt` guest client — and it needs **no Asterisk and no radio hardware** ("radio-less"). Audio comes from the device microphone/speaker or a paired Bluetooth speaker-mic/headset whose hardware button acts as push-to-talk (PTT).

Primary use case: a **portable node on a phone or laptop** that registers with AllStarLink, links *outbound* to other nodes, and stays connected while the screen is off and while the device moves between Wi-Fi and cellular.

Design stance: **outbound-linking only.** Carrier-grade NAT blocks unsolicited inbound IAX2 on cellular, so NodeX dials out and ignores inbound link attempts (an inbound listener would only be possible over a VPN overlay such as WireGuard/Tailscale; not implemented).

---

## 2. Status snapshot

| Area | State | Verified how |
|---|---|---|
| IAX2 protocol engine (framing, reliability, VNAK, keep-alive, LAG, call token) | Implemented | Unit + loopback tests against a scripted fake node |
| ASL HTTPS registration | Implemented to the real ASL3 wire format | Request format read from ASL3 source; tested against a stub server |
| Node resolution | Implemented; REST endpoint and DNS fallback **verified live** (§14) | Stubbed tests + live check |
| PTT state machine (momentary/toggle, TOT, sources) | Implemented | Tests |
| Audio pipeline (jitter buffer, resampler, levels, engine) | Implemented | Tests with fake devices |
| Settings + secure password storage | Implemented | Tests; first-run screen visually checked on Windows |
| `NodeRuntime` (registration, link, **auto-reconnect, network handoff**) | Implemented | Tests with fake node + fake network |
| **Multiple nodes at once** (up to 8; one transmit node, per-node mute) | Implemented; owner has used multiple links on Android | Fake-node tests; maximum-load/mixing behavior is not separately measured |
| Desktop head (Windows) | Runs; NAudio `WaveIn`/`WaveOut` audio; mic and speaker **verified** via `LiveCheck` (the full GUI app has not been used for an on-air QSO) | Launched and screenshot-checked; live audio checks |
| Desktop head (Linux/macOS) | PortAudio, device pickers, OS keyring and macOS packaging coded | Pi harness only; full app/audio/keyring/Mac bundle not run |
| Android head | Available on Play internal testing and used on the owner's phone | Owner reports working screen-off audio, handoff and good voice quality (§2); individual BLE/permission scenarios remain to verify |
| iOS head | Compiles/builds; **never run on a device**; release prepared but not started, needs paid Apple account + Mac (§3.6, `docs/IOS_RELEASE.md`) | Build only |
| Bluetooth PTT | Android, iOS and desktop transports + shared Learn wizard implemented | Core fake-driver/manager tests; no real button tested |
| Responsive/phone layout | Implemented (single-column phone layout with docked PTT bar below 720 px or below 520 px height; two-column desktop above). **Verified only by resizing the Windows app (340–1100 px wide, 860×420 landscape), not on a real phone** | Screenshots at 340 / 410 / 780 / 1100 px and 1050×760, 800×760, 420×860, 860×420 |
| Live-node interoperability | **Verified against owner's cloud node 523800 (2026-11-05):** registration, call-token handshake, `radio` login, keep-alive, keying, DTMF `*70`, mic audio TX, re-dial (same IP). Owner later reports successful handoff and good on-air voice. **Not instrumented:** different-IP timing, controlled audio comparison, other app_rpt versions | `tools/NodeX.LiveCheck`; log in `docs/LIVE_VALIDATION.md` |
| Optional station-ID reminder (§17) | Implemented on all heads' shared UI (prompt in the hero card + activity-log line) + **Android/iOS background notification (`AppServices.IdReminderNotifier`) wired**; not visually checked on a phone | Unit tests (`IdReminderTests`), solution builds |
| Transmit voice clean-up (`MicProcessor`) | Implemented | Unit tests; owner reports good on-air quality |

**Owner field reports (2026-11-11, on the owner's phone/PC; owner's word, not instrumented):** screen-off background audio for 1 h or more works; Wi-Fi to cellular handoff works, with a delay while the new public IP registers with AllStarLink (so the different-IP handoff is now seen to work, with that delay); the `MicProcessor` voice clean-up sounds good on air, no adjustment needed. **Latest owner report: the Desktop Head Unit is working great.** This is a successful DHU report, not an itemized verification of every car/voice/focus scenario. **Still not tested by anyone:** BLE PTT button and headset media key (no hardware; needs a volunteer), the full Linux/macOS app (planned for community testers; Pi audio harness and Windows BLE scan already checked), itemized real-car checks and safe-area/keyboard behaviour on assorted phones (the notch/gesture-bar overlap and whether the on-screen keyboard covers the PTT bar or node field).

---

**2026-11-12 car follow-up:** the owner reports that the latest Play install lost Talk/wheel PTT, showed a white icon and appeared to dial nodes on Next. Code confirms that Play deliberately disables car PTT; the 1.16 fix separates the color launcher icon from monochrome attribution. “Transmit here” is a different action shown for a non-transmit node. Current Next/Back code only changes the displayed existing link; fresh dialing was not reproduced or explained. Real-car confirmation of the icon fix and actual installed-build/log evidence for any new dialing remain outstanding.

## 3. Repository, tooling and build

### 3.1 Layout (authoritative file map)

```
NodeX/
├── Directory.Build.props            # redirects bin/obj OUT of OneDrive (see 3.3) — keep!
├── NodeX.sln                        # Core, UI, Desktop, Android, iOS, Tests
├── PROJECT_SPEC.md                  # this file
├── docs/LIVE_VALIDATION.md          # dated log of tests against real nodes/hardware
├── tools/NodeX.LiveCheck/           # console harness: resolve/register/link/redial/audio/tone (drives the real Core engine)
├── src/
│   ├── NodeX.Core/                  # net10.0 class library; no UI/platform dependencies
│   │   ├── Protocol/
│   │   │   ├── Iax2Protocol.cs          # frame/IE (de)serialisation, enums (RFC 5456 tables)
│   │   │   ├── Iax2Session.cs           # UDP socket, handshake, reliability, keep-alive, audio frames
│   │   │   ├── Iax2Sequencing.cs        # internal: modulo-256 rules, which frames are reliable
│   │   │   ├── Iax2RetransmitQueue.cs   # internal: unacked-frame queue, back-off, retry limit
│   │   │   └── Codecs/G711Ulaw.cs       # zero-allocation µ-law tables
│   │   ├── Services/
│   │   │   ├── AslRegistrationService.cs  # HTTPS registration (ASL3 wire format), retry and blip tolerance
│   │   │   ├── NodeResolution.cs          # node number -> IP: static overrides, cache, REST, DNS fallback, last known
│   │   │   ├── DnsPortLookup.cs           # IAX port from AllStarLink DNS (SRV/TXT) by raw UDP DNS
│   │   │   ├── NodeDirectory.cs  UsStates.cs  # "Find a node" search (stats site list feed), US state matching
│   │   │   └── Iax2Registrar.cs           # opt-in IAX2 REGREQ registration over UDP (no UI, never run live)
│   │   ├── Audio/
│   │   │   ├── IAudioDeviceService.cs   # platform audio contract (8 kHz mono PCM16)
│   │   │   ├── IVoiceChannel.cs         # what the engine needs from a call
│   │   │   ├── NodeAudioEngine.cs       # capture->µ-law->net, net->jitter->playout->device
│   │   │   ├── JitterBuffer.cs  PcmFramer.cs  PcmResampler.cs  AudioLevelMeter.cs
│   │   │   ├── MicProcessor.cs          # transmit voice clean-up (high-pass, AGC, soft limiter)
│   │   │   ├── AudioDeviceOption.cs     # one entry of the desktop device pickers
│   │   │   └── NullAudioDeviceService.cs  # silent fallback / designer
│   │   ├── Link/
│   │   │   ├── NodeLink.cs              # ONE call = Session + Ptt + Audio (single-use)
│   │   │   ├── NodeRuntime.cs           # config + registration + active link + reconnect + handoff
│   │   │   ├── NodeConnection.cs        # one wanted node, kept across re-dials (reconnect state, transmit/mute)
│   │   │   ├── NetworkMonitor.cs        # INetworkMonitor + SystemNetworkMonitor
│   │   │   ├── ReconnectPolicy.cs       # timing + ReconnectStatus
│   │   │   ├── BackgroundState.cs       # what the OS must keep alive (stay registered / linked)
│   │   │   ├── RxActivity.cs            # "is this node being heard" rule (KEY-aware)
│   │   │   └── CarBrowse.cs  CarTransmit.cs  MediaKeyTransmit.cs   # Android Auto and headset-key rules (pure logic)
│   │   ├── Settings/
│   │   │   ├── NodeSettings.cs  NodeSettingsValidator.cs  Stores.cs  NodeConfigService.cs  SettingsTransfer.cs
│   │   ├── Diagnostics/DiagnosticLog.cs # rolling troubleshooting log, secrets redacted
│   │   ├── Bluetooth/IBluetoothPttService.cs  BluetoothPttPacketParser.cs  IBlePttTransport.cs  BlePttManager.cs  BlePttLearner.cs  BlePttProfile.cs
│   │   ├── NodePttController.cs         # PTT state machine
│   │   ├── TransmitPolicy.cs  HardwarePttMapper.cs  BuildFlavor.cs  DurationText.cs   # listen-only/mic gate, phone-button PTT, Play edition switch, "2 h 05 min"
│   │   └── IdReminder.cs                # optional "time to identify" reminder (UI prompt only; transmits nothing)
│   └── NodeX.UI/                    # own Directory.Packages.props (central package versions)
│       ├── NodeX.UI/                    # shared Avalonia views + view models
│       │   ├── AppServices.cs               # composition root; heads call Initialize(...)
│       │   ├── App.axaml(.cs)               # palette (brushes), startup, orderly shutdown
│       │   ├── ViewModels/MainViewModel.cs  SettingsViewModel.cs  ViewModelBase.cs
│       │   └── Views/MainView.axaml  SettingsView.axaml  MainWindow.axaml
│       ├── NodeX.UI.Desktop/            # Program.cs, Audio/NAudioDeviceService (Windows), Audio/PortAudioDeviceService (Linux/macOS), Security/ProtectedFileSecretStore
│       ├── NodeX.UI.Android/            # Application, MainActivity, Audio/, Security/, Services/ (foreground svc, locks, SCO)
│       ├── NodeX.UI.iOS/                # AppDelegate, Audio/ (session manager + AVAudioEngine device), Security/ (Keychain)
└── tests/NodeX.Core.Tests/          # xUnit; references Core AND NodeX.UI (for view-model tests)
```

### 3.2 Targets and tooling
- `net10.0` (Core, UI, Desktop, tests), `net10.0-android` (**minSdk 26**, Android 8.0), `net10.0-ios` (min iOS 13).
- All projects are SDK-style. SDK in use: **.NET 10.0.403** (a 6.0 SDK is also installed; `global.json` pins 10.0.403 and rolls forward within the feature band).
- Avalonia **12.1.5**, CommunityToolkit.Mvvm 8.4.2 (uses C# partial properties: `[ObservableProperty] public partial string X { get; set; }`), NAudio 2.2.1 (Windows audio), System.Security.Cryptography.ProtectedData (DPAPI). Central package management lives in `src/NodeX.UI/Directory.Packages.props`.
- Workloads installed on the dev machine: android, ios, maui-windows. iOS **compiles** on Windows; producing a signed `.ipa` needs a Mac with Xcode.
- Git: private repo `Russell-KV4S/NodeX` on GitHub (initialised 2026-11-05, branch `main`). `.gitignore` excludes `bin/`, `obj/`, local config and secrets. The checkout is inside OneDrive (see §3.3); `.git` lives there too.

### 3.3 Build output location — IMPORTANT
The repo lives in a **OneDrive** folder. OneDrive pins `bin`/`obj` directories as read-only cloud-file reparse points, which made Android/iOS builds fail intermittently with "Access to the path … is denied" (`MSB3231`, `XARDF7024`) — *not* code errors. `Directory.Build.props` therefore redirects all intermediate/output paths to **`%LOCALAPPDATA%\NodeX\build\{obj,bin}\<ProjectName>\`** (override with the `NODEX_BUILD_ROOT` environment variable). Consequences:
- Built binaries are **not** under the project folders. Desktop exe: `%LOCALAPPDATA%\NodeX\build\bin\NodeX.UI.Desktop\Debug\net10.0\NodeX.UI.Desktop.exe`.
- If you ever see lock errors again: `attrib -R -P +U "<dir>\*" /S /D` then delete the directory.
- Do not create `bin`/`obj` folders inside the repo.

### 3.4 Commands
```bash
dotnet build NodeX.sln                              # everything (Core, UI, Desktop, Android, iOS, tests)
dotnet test  NodeX.sln                              # or: dotnet test tests/NodeX.Core.Tests
dotnet test  tests/NodeX.Core.Tests --blame-hang-timeout 60s   # recommended: surfaces hangs instead of waiting forever
dotnet run   --project src/NodeX.UI/NodeX.UI.Desktop           # launch the desktop app
dotnet build src/NodeX.UI/NodeX.UI.Android          # Android head
dotnet build src/NodeX.UI/NodeX.UI.iOS              # iOS head (compile check on Windows)
```
In Visual Studio set **`NodeX.UI.Desktop`** as the startup project. The settings file lives at `%LOCALAPPDATA%\NodeX\settings.json` (created only after the first successful Save).

### 3.5 Android release to Google Play (internal testing)
- **Two editions (2026-11-11):** the Play build is made with `-p:PlayStore=true` and has no transmit-from-the-car (Android Auto Talk and wheel talk are compiled off via `BuildFlavor.CarTalkAllowed`); the full edition goes to GitHub Releases as a sideload APK. Same branch, no second branch to sync. Commands and checklist: [`docs/RELEASING.md`](docs/RELEASING.md). Reason: Google may reject a media-category app that transmits from the car.
- **Identity:** `ApplicationId` = **`com.kv4s.nodex`** (permanent once uploaded; the foreground service and its intent actions use the same prefix). Play Console app **NodeX** under the developer account **RT App Works**; the app is free (a free app cannot later become paid). Donations, if ever added, would need a policy check first (Play Billing tip jar vs external link is unconfirmed).
- **Format and signing:** Release builds produce an **`.aab`** (Debug stays `.apk`). `NodeX.UI.Android.csproj` imports **`keystore/signing.props`** for Release; `keystore/` is **gitignored** and holds `nodex-upload.keystore` (alias `nodex-upload`, ~27-year validity, upload key under Play App Signing) plus the passwords in `signing.props`. **Back the folder up; it is the only copy.** Never commit it.
- **Build:** bump **`NodeXVersionCode`** (must increase every upload) and `NodeXVersion` in `Directory.Build.props`, then `dotnet publish src/NodeX.UI/NodeX.UI.Android -c Release -p:PlayStore=true`. Output: `%LOCALAPPDATA%\NodeX\build\bin\NodeX.UI.Android\Release\net10.0-android\com.kv4s.nodex-Signed.aab` (~45 MB). Copy it to `keystore/nodex-<ver>-code<N>.aab` and check with `jarsigner -verify`.
- **Upload is manual:** the Play Console file picker cannot be driven by an agent (the browser upload tool caps files at 10 MB). The owner uploads the `.aab` under Testing → Internal testing → Create new release; an agent can fill the release name and notes (each `<en-US>` tag must be on its own line) and publish.
- **Testers:** email list **NodeX testers** (owner only, saved as the track's list). The shared **Honey and Blush testers** list belongs to another app and must not be ticked here. Opt-in link: `https://play.google.com/apps/internaltest/4699734114435882711`. The desktop store page says "Not Found" for a test-only app; install on the phone. The first release took a while to appear.
- **Releases so far:** 1.0 (code 1) and **1.1 (code 2)**, 2026-11-05 (listen-only, required callsign, Find-a-node, stay-registered + Exit, phone-button PTT, Toggle PTT). Play's two warnings (no deobfuscation file, no native debug symbols) are harmless: minification is off.
- **Release prep (2026-11-11):** drafts in [`docs/PRIVACY.md`](docs/PRIVACY.md) and [`docs/PLAY_STORE_LISTING.md`](docs/PLAY_STORE_LISTING.md) (listing text, foreground-service justifications, data-safety answers; none submitted); four demo-data phone-layout screenshots in `docs/screenshots/` (Windows app, demo settings, never connected). Play-edition AAB built as 1.15 (code 17) at `keystore/nodex-1.15-code17-play.aab`, `jarsigner -verify` OK; not uploaded; Android Auto Talk removal in that build is by the compile switch only, not exercised on a device. That bundle predates the current changes/test suite.
- **1.17 (code 19, 2026-11-12):** Play edition built at `keystore/nodex-1.17-code19-play.aab` and the full sideload APK at `keystore/nodex-1.17-code19-full.apk` (both `jarsigner -verify` OK, from commit `67c0543`). **The Play `.aab` has not been uploaded yet** (owner uploads; the 1.15 bundle was the last one on Play). Standing rule: an "Android bundle" request means both files in `keystore/` plus a GitHub release.
- **Still needed before any wider track:** privacy policy URL, data-safety form, content rating, target audience, store listing, and **foreground-service declarations** (microphone, media playback, connected device, special use) with justifications. A newer personal developer account may also need a closed test (about 12 testers for 14 days) before production.

### 3.6 iOS release (TestFlight) — **not started; handover only**
Owner has no Mac and declined the $99/year Apple Developer Program (2026-11-06), so there is no iOS build, signing setup or TestFlight. The head is prepared (bundle ID `com.kv4s.nodex`, `Info.plist` fixed, 1024 icon set added, compiles on Windows). **Full requirements, build/upload commands and open gaps are in [`docs/IOS_RELEASE.md`](docs/IOS_RELEASE.md)** so anyone with an Apple account (or a cloud Mac / GitHub Actions macOS runner) can pick it up. Asset catalog and signing properties are unverified until a Mac build.

### 3.7 GitHub releases and Android sideload updates (2026-11-12)

**Owner requirement:** publish downloadable builds for every supported platform except iOS. GitHub Android downloads must be the **full edition**, with Android Auto Talk and steering-wheel PTT available (still off until enabled in Settings). Google Play remains a separate listen-only car edition. Repository and releases remain **private by the owner's explicit decision**; public access is pending. Do not make the repository public or embed a GitHub token in the app.

**Release pipeline:** `.github/workflows/release.yml` runs on version tags (`v1.16`, etc.) or manual dispatch for an existing tag. `Directory.Build.props` supplies `NodeXVersion` and the monotonically increasing `NodeXVersionCode`; Android and desktop consume them. The tag must match that version. Core and headless UI tests gate signed full-edition Android APK and self-contained desktop packages for Windows x64/ARM64, Linux x64/ARM64, macOS Apple Silicon/Intel. No iOS artifact is produced. All seven packages must succeed before the release is published. A draft is populated first, with `nodex-update.json` and `SHA256SUMS.txt`, then published. Published releases must never be overwritten: increment both versions for another release. Signing uses the existing Android key through GitHub Actions secrets; never commit it, and never publish a debug-signed APK. Apple packages are ad-hoc signed only, not Developer ID signed/notarized. Hardware validation remains outstanding on Linux/macOS and for Android update installation.

**Stable assets:** `NodeX-android.apk`, `NodeX-win-x64.zip`, `NodeX-win-arm64.zip`, `NodeX-linux-x64.tar.gz`, `NodeX-linux-arm64.tar.gz`, `NodeX-osx-arm64.zip`, `NodeX-osx-x64.zip`. Packages retain NodeX's license and desktop third-party notices, including SimpleBLE's separate terms. Intel macOS has no bundled SimpleBLE backend; Apple Silicon BLE requires macOS 15+. A platform build is not proof of working audio/BLE on that platform.

**Android updater, first implementation:** full Android only. A foreground open/resume checks at most once per day per running process; Settings → App updates also permits an explicit check. The phone displays an available-update notice linking to Settings. No download or install starts automatically; checks neither dial nor disconnect nodes. The app reads only the fixed repository's latest stable release, ignores drafts/prereleases, and requires a complete manifest/APK pair. The versionCode must exceed the installed code (same/older versions are never offered). The manifest's version must match the tag, and the manifest must identify `com.kv4s.nodex` and the fixed APK asset. Size is bounded to 512 MiB, JSON to 1 MiB, and the APK's declared size and SHA-256 must match before a private-cache `.part` becomes an installable file. Failures/cancellation remove partial downloads. Manual downloads can be cancelled and have a 15-minute timeout; checks time out after 30 seconds.

**Installation:** refuse while any node is connecting/linked/reconnecting or Android Auto is active; check again after downloading. Validate APK package ID, versionCode and signing certificate against the installed full edition; rehash the cached file before launching installation. Share only the `cache/updates/` subtree using a non-exported FileProvider with temporary read permission. Android 8+ requires the user's “Allow from this source” permission; after granting it, return to NodeX and tap install again. Android's package installer always requests final confirmation. Same-key in-place updates preserve settings and Keystore-protected passwords. Do not rotate the signing key in this initial implementation. No silent updates or automatic app restart. Play builds compile out the updater entry point/provider and omit `REQUEST_INSTALL_PACKAGES`; iOS/desktop have no self-installer in this first phase.

**Private-feed limitation:** the app has no GitHub authentication, so private releases cannot be checked/downloaded in-app. A 404 reports that the repository may be private or have no release; it must not falsely say “up to date.” Authorized testers can download the APK from GitHub manually. Enabling anonymous public access (or designing a separately authenticated feed) is a prerequisite for in-app delivery. Switching Play ↔ sideload may require uninstalling because of different signing certificates: export settings first and be prepared to re-enter the node password. The updater never tries to replace a Play install.

**Deferred:** persistent check scheduling across process restarts, background delivery, desktop update installation, Apple Developer ID signing/notarization, and private-feed user authentication. Verify the icon, permissions flow, cancelled installs, same-key upgrade with preserved settings/password, and wrong-key rejection on real Android hardware before claiming device validation. Current implementation tests cover feed/version/asset validation, corruption/size/cancellation cleanup, download consent, check throttling and the link/car installation gate.

**First release:** [v1.16 / Android code 18](https://github.com/Russell-KV4S/NodeX/releases/tag/v1.16) was published privately from `f10deba`; [all release jobs passed](https://github.com/Russell-KV4S/NodeX/actions/runs/43296269873). Android's single APK includes ARM64 and x86-64, matching the current .NET Android default RIDs (not 32-bit ARM). Downloaded APK v2/v3 signatures, package/version and published manifest size/SHA-256 were verified; Windows/Mac package contents and Mac bundle version were checked. This does not validate execution, audio, installation or Android Auto on hardware.
**Second release:** [v1.17 / Android code 19](https://github.com/Russell-KV4S/NodeX/releases/tag/v1.17) was published privately from `67c0543` (the nodes-dropping fixes and mute-instead-of-hang-up car media commands). The [Release run](https://github.com/Russell-KV4S/NodeX/actions/runs/43306377062) and CI passed; the published release lists the Android APK, six desktop packages, `nodex-update.json` and `SHA256SUMS.txt`. Contents were checked by asset list only, not downloaded and inspected. The Windows build was deployed to `C:Program FilesNodeX` on the owner's PC the same day (exe reports 1.17); nothing has yet been run with live nodes, on a phone or in a car.

---

## 4. Architecture as built

### 4.1 Layering
```
 UI (Avalonia, MVVM)           MainViewModel, SettingsViewModel  ── bind to ──►  NodeRuntime events
        │                                                                              │
 AppServices (composition root; heads inject secure storage + audio backend)           │
        │                                                                              ▼
 NodeRuntime ── owns ──► NodeConfigService (settings JSON + secret store)
        │      ── owns ──► AslRegistrationService (HTTPS, ~60 s)
        │      ── owns ──► INetworkMonitor, reconnect/handoff logic
        └─ one NodeConnection per wanted node (max 8) ─► per call: NodeLink = Iax2Session + NodePttController + NodeAudioEngine(+ IAudioDeviceService)
```
`NodeX.Core` has **no** dependency on Avalonia or any platform API. Platform heads implement `IAudioDeviceService` and `ISecretStore` and register them via `AppServices.Initialize(secrets, audioFactory, audioOptions)` **before** Avalonia starts (`Program.Main` / `Application.CustomizeAppBuilder` / `AppDelegate.CustomizeAppBuilder`).

### 4.2 `Iax2Session` (Core/Protocol) — one call over UDP
Single-use: it owns a socket and four background loops (receive, keep-alive, retransmit, link-list). **Create a new session per call; never reconnect a used one.**
- **Handshake:** first `NEW` carries an *empty* CALLTOKEN IE; on a `CALLTOKEN` reply it clears pending frames, resets sequence numbers/remote call number and re-sends `NEW` with the token; `ACCEPT` → `Connected`. `AUTHREQ`/MD5 auth is implemented (not needed for node links).
- **Reliability (RFC 5456 §7):** every full frame except ACK/INVAL/TXCNT/TXACC/VNAK/CALLTOKEN is sequenced (8-bit, wraps) and queued until the peer's ISeqno acknowledges it; retransmitted with the R bit on a timer (initial `InitialRetransmitTimeout` 1 s until an RTT is measured, then 2×RTT clamped 100 ms–10 s; doubles per retry, cap 10 s); after `MaxRetransmits` (4) the call is declared lost. Inbound: out-of-order (future) frames are ignored and answered with **VNAK**; duplicates are re-ACKed and not re-processed; ACKs are never ACKed.
- **Keep-alive:** `PING` every `KeepAliveInterval` (15 s); the peer's `PONG` echoes our timestamp → RTT (`RoundTripTime`, `RoundTripTimeUpdated`). **Watchdog:** any valid inbound packet resets a counter; `MaxMissedKeepAlives` (3) silent intervals → link lost. `POKE`/`PING` from the peer are answered with `PONG`; `LAGRQ` with `LAGRP` (timestamp echoed).
- **Validation:** only packets from the dialled endpoint are accepted; frames must carry our call number as destination and the peer's call number as source; mini frames must match the established peer.
- **Audio frames:** the first voice frame of each transmission (and every 16-bit timestamp wrap) is sent as a *full* Voice frame (subclass = codec 0x04 µ-law); subsequent frames are 4-byte-header **mini frames** (low 16 bits of the ms timestamp).
- **Disconnect reason:** `DisconnectReason` ∈ {None, LocalRequest, RemoteHangup, RemoteRejected, RemoteBusy, LinkLost}, set *before* `StateChanged(Disconnected)`. Only `LinkLost` is auto-recoverable.
- **Threading rule:** events (`StateChanged`, `RemoteKeyedChanged`, `VoiceFrameReceived`, …) fire on the **receive/keep-alive/retransmit threads**. Never dispose the session from inside a handler (it used to deadlock; `DisposeAsync` now bounds its waits at 2 s, but handlers should still hand off to another task, as `NodeRuntime` does).
- **Robustness rules (2026-11-11, all unit-tested):**
  - *No loop may die from one bad event.* The receive loop survives a socket error (Windows reports ICMP "port unreachable" as a receive failure; that is switched off with `SIO_UDP_CONNRESET` and tolerated anyway; only about 2 s of nothing but errors ends the call as `LinkLost`) and a frame whose handling throws (reported as "IAX2 frame handling error", the frame is lost, the call is not). The keep-alive, link-list and retransmit loops treat a send that fails as a lost packet instead of ending.
  - *Disconnected is final.* State changes go through one lock; once a dialled session is `Disconnected` nothing brings it back (a late `ACCEPT`/`ANSWER` is ignored), the reason is the first one recorded and `StateChanged(Disconnected)` is raised once. A used session refuses `ConnectAsync`. `ACCEPT`, `ANSWER` and `AUTHREQ` only act while the call is being set up; a `CALLTOKEN` frame is only honoured before the peer has answered (it bypasses the call-number check, so mid-call it used to reset the sequence numbers and send a second `NEW`). While hanging up or after the end, inbound frames are still acknowledged but nothing in them is acted on.
  - *Sequence numbers have one owner:* every read and change of `OSeqno`/`ISeqno` (sequenced frames, ACK/VNAK headers, the inbound increment, the call-token reset) happens under `_sendLock`.
  - *Each `StateChanged` listener is called on its own*, so one that throws cannot stop the runtime hearing that a call ended.
  - *HANGUP is delivered, not just sent.* `DisconnectAsync` re-sends HANGUP every 100 ms until the peer acknowledges it (or answers `INVAL`), for at most `HangupAckWait` (300 ms), then closes anyway. `DisposeAsync` hangs up before it stops the loops so the acknowledgment can be seen. `NodeRuntime` sets the wait to zero for links it ends because the network changed, and hangs several links up in parallel.
  - *Authentication:* only the MD5 challenge is answered; see §10.
- **Limitations:** IPv4 only; binds `preferredLocalPort` when specified and free, falling back to an ephemeral UDP port when 0 or in use; G.711 µ-law only; no call transfer; `AST_FRAME_TEXT` (`!NEWKEY1!`, `L <links>`, `T <node> STATUS,…`) is parsed by `AslTextParser` and surfaced on `NodeLink.RemoteLinkedNodes` / `LinkItemViewModel.RemoteLinksText` (see §14).

### 4.3 `NodePttController`
Sources: UI button, Bluetooth, media key, VOX. Modes: **Momentary** (hold) / **Toggle** (tap to start, tap again to stop; UI release ignored; media key always toggles). A **Time-Out Timer** (default 180 s, per-key-up restart) force-unkeys with source `External`. Sends `AST_CONTROL_KEY` (0x0C) / `UNKEY` (0x0D) through the session; `SetPttStateAsync` is idempotent and thread-safe. Disposal unkeys. **`HandleButtonAsync(pressed, source)` (2026-11-06)** is the one place a physical button follows the PTT mode (Momentary: hold to key; Toggle: each press flips, release ignored); Bluetooth and the mapped phone button both use it. **`Policy` (`TransmitPolicy`, 2026-11-06):** keying is refused (with a `WarningLogged` explaining why) while transmit is blocked, and an active key is released if it becomes blocked; unkeying is never blocked. `TransmitPolicy` (owned by `NodeRuntime.Transmit`) blocks when `ListenOnly` (from settings) or `MicrophoneAllowed` is false (set by a platform head; Android does this from `RECORD_AUDIO`). **Listening never depends on it**: with the mic denied or listen-only the app still connects and plays received audio (the mic is opened only while keyed, so it is never opened). **`TransmitEnabled` / `ListenOnlyReason` (2026-11-06):** a per-link gate, default true. With several nodes linked `NodeRuntime` sets it true only on the transmit node's link; keying a listen-only link is refused with a `WarningLogged` ("Node N is listen-only. Choose it as the transmit node…"), and clearing it on a keyed link unkeys it. It is independent of `Policy`. **Connected-only and serialised (2026-11-11):** a key is refused (with a `WarningLogged`) unless the session is `Connected`, and key/unkey requests run one at a time so the controller and the session can never disagree about being keyed; see the change log.

### 4.4 Audio pipeline (Core/Audio)
- **Format:** 8000 Hz, 16-bit, mono, 20 ms frames (160 samples) everywhere in Core (`AudioFormat`).
- **`IAudioDeviceService`** (implemented per platform): `StartCapture(Action<ReadOnlyMemory<short>>)`, `StopCapture`, `StartPlayback`, `StopPlayback`, `EnqueuePlaybackPcm`, `BufferedPlayback`, `ErrorOccurred`. Capture callbacks arrive on the driver's audio thread with arbitrary block sizes.
- **`NodeAudioEngine`:** capture → `PcmFramer` (exact 160-sample frames) → mic gain → level meter → (only while PTT keyed) µ-law → bounded drop-oldest queue (8) → `Session.SendAudioFrameAsync`. Receive: `VoiceFrameReceived` → µ-law decode → `JitterBuffer` → 20 ms playout clock (5 ms timer, catch-up, stall protection) → device. Skips frames if the device backlog exceeds 200 ms. `KeepAudioPipelineAlive` writes zero PCM during silence (**iOS keeps its audio session/background execution alive this way**).
- **Start and stop never interleave (2026-11-12):** `Start`, `StopAsync` and `DisposeAsync` share one lock, and `Start` does nothing after disposal. A call hung up about 100 ms after it was accepted starts its audio on a worker thread while the runtime disposes it; the two used to interleave, which showed on a phone as "Audio could not start: Object reference not set to an instance of an object" and could open the playback device after it had been stopped. The new tests fail with that exact exception on the old code.
- **`JitterBuffer`:** timestamp-keyed (16-bit wrap safe), prime depth 3 frames (60 ms), capacity 25, reorder, drop late/duplicate, conceal up to 3 lost frames by repeating the last frame with halving gain, re-prime after idle; thread-safe.
- **`PcmResampler`:** streaming area-averaging resampler to 8 kHz (used on iOS capture; hardware is usually 44.1/48 kHz). **`AudioLevelMeter`:** RMS/peak with attack/release ballistics; UI maps RMS −60…0 dBFS to 0…1.
- **Transmit voice clean-up (`MicProcessor`, added 2026-11-05 after the first on-air test sounded "mostly clear"):** G.711 does nothing to the signal (8 kHz sampling + log quantisation), and the first live test sent the raw mic at only −28 dBFS peak. Per 20 ms frame, after `MicGain` and before the VU meter/encoder (`NodeAudioEngineOptions.MicProcessing`, driven by the `MicProcessing` setting, default **on**, checkbox in Settings): (1) 2nd-order Butterworth **high-pass at 300 Hz**; (2) slow **AGC** towards −21 dBFS RMS (≈ −6 dBFS speech peaks): gain range −12…+24 dB, attack 0.5/frame, release 0.03/frame (~0.7 s), **gate at −50 dBFS** — pauses/noise never raise the gain and it drifts back to 0 dB (0.02/frame) so hiss is not left boosted; gain is ramped across each frame (no clicks); (3) **soft limiter** (tanh knee from 70% of a 0.89 FS ceiling) instead of hard clipping. Unit-tested (rumble −18 dB+, 1 kHz within 1.5 dB, quiet voice lifted to target, loud voice tamed under the ceiling, pauses not amplified, silence stays silence, ramped gain). **Owner reported good on-air voice quality on 2026-11-11.** A controlled A/B comparison is optional.
- **Voice timestamps (`Iax2Session.NextVoiceTimestamp`):** mini-frame timestamps used to be the wall clock at send time, which measured live as 16–24 ms spacing around a nominal 20. Now each voice frame is stamped previous + its own duration (20 ms), nudged ≤ 1 ms/frame towards the wall clock when it is > 5 ms away (so drift never accumulates), and restarted from the wall clock after a gap > 100 ms or at the start of an over. Unit-tested (jitter → 19–21 ms spacing, 1 % clock drift stays within 8 ms over 60 s, gap resync, 16-bit wrap).
- **Several links = several engines (2026-11-06).** Each `NodeLink` keeps its own `NodeAudioEngine` **and its own `IAudioDeviceService`** (the runtime calls the audio factory once per link); the OS mixes the outputs (WaveOut / AudioTrack / AVAudioEngine each play independently), so nothing in the verified single-link audio path changed. Only the transmit link ever opens the mic (it opens only while keyed), so there is still one capture. `NodeAudioEngine.Muted` silences one link: audio is still decoded, metered and reported via `RemoteAudioActiveChanged` (the row still shows "Receiving"), but nothing is enqueued to the device (zeros instead when `KeepAudioPipelineAlive`, so iOS keeps its session). **No cross-link repeating:** audio from one node is never forwarded to another (NodeX is an endpoint, not a hub). Android multi-link use is owner-reported; maximum-load mixing is not separately measured. iOS multiple engines remain unverified. Open speakers can increase echo.
- **Known behaviour:** all heads (desktop, Android, iOS) set `OpenMicOnlyWhileKeyed`, so the mic is open only while keyed (+3 s hold-off) — see §19–20. The input meter is idle while the mic is closed. Desktop enabled 2026-11-06; not yet heard/checked live (first words after keying may clip).
- **Echo (desktop):** there is no acoustic echo cancellation on desktop; with open speakers and a live mic the received audio will be re-transmitted. Use a headset. Mobile uses the platform's voice-communication processing (§7). Not yet addressed.

### 4.5 `NodeLink` and `NodeRuntime` (Core/Link)
- **`NodeLink`** = one call: `Iax2Session` + `NodePttController` + `NodeAudioEngine` + its device. Logs in as IAX user **`radio`** with `CALLING_NUMBER` = local node (see §5.4). Audio starts when the session reaches `Connected` (off the receive thread). `WasConnected` records whether it ever connected.
- **`NodeRuntime`** is the UI-toolkit-independent coordinator. API: `StartAsync`, `ApplyConfigAsync`, `ConnectAsync(targetNode)`, `DisconnectAsync`, `Link`, `Config`, registration state (`IsRegistered`, `RegistrationStatus`, `PerceivedAddress`), and events `RegistrationChanged`, `LinkChanged`, `LinkActiveChanged(bool,node)`, `ReconnectChanged(ReconnectStatus)`, `ErrorOccurred`, `Notice`. Saving settings (via `NodeConfigService.Changed`) re-applies configuration and (re)starts registration automatically.
- **`LinkActiveChanged`** means "a node link is *wanted*" — it stays `true` across automatic reconnects so platform heads don't tear down background services mid-recovery. Android uses it to start/stop the foreground service. With several nodes the node argument (and `BackgroundState.LinkedNode`) is a comma-joined list, e.g. "27339, 55553", so the notification reads "Linked to node 27339, 55553".
- **Multiple nodes at once (2026-11-06; design: Flowstep "NEW Multi-node links - phone").** `NodeRuntime` keeps one **`NodeConnection`** per wanted node (public read-only view: `Node`, `Info`, `Link`, `Reconnect`, `IsTransmitTarget`, `Muted`); each owns its own reconnect loop, early-hang-up counter and `Wanted` flag — the per-link logic of §4.6 is unchanged, just per node. API: `ConnectAsync(node)` **adds** a link (max `MaxLinks` = 8, raised from 4 on 2026-11-09, else `InvalidOperationException`); connecting to a node that is already linked redials just that node and keeps its transmit/mute choice; `DisconnectAsync()` hangs up all, `DisconnectAsync(node)` one; `Connections`, `Links` (live calls), `TransmitNode`, `SetTransmitNode(node?)`, `SetMuted(node, bool)`; events `LinkAdded`, `LinkRemoved`, `ConnectionsChanged` (any change: set, state, transmit, mute, reconnect). **`Link` still exists and means the transmit node's live call** (null when none/between attempts); `LinkChanged` was removed (replaced by `LinkAdded`/`LinkRemoved`); `Reconnect`/`ReconnectChanged` report the first node that is recovering (per node: `NodeConnection.Reconnect`).
  - **Transmit rules (safety):** exactly one link may key. The first node linked becomes the transmit node; later ones are listen-only until the user picks one (`SetTransmitNode`; an active key on the old node is released). If the user hangs up the transmit node and exactly one link remains, that one becomes the transmit node; **if the far end drops the transmit node, the choice is left empty** (notice "Node N ended. Choose a node to transmit to.") and PTT is inert until the user chooses — a transmission is never silently redirected to a different RF node. A transmit node that is only reconnecting stays the transmit node. DTMF (keypad and macros) goes to the transmit node only.
  - **Network handoff** now re-dials every established link (each node recovers on its own loop); registration is refreshed once.
  - Errors from a link are prefixed "Node N: " when more than one node is linked.

### 4.6 Auto-reconnect and network handoff (spec of record)
**Auto-reconnect** (setting `AutoReconnect`, default on) re-dials when an **established** link is lost:

| Link ended because… | Re-dial? |
|---|---|
| Keep-alive watchdog / retransmit limit / INVAL (`LinkLost`) **and** the link had been `Connected` | **Yes**, with back-off |
| `LinkLost` but the call was **never answered** | No — error "No response from node N" |
| Remote hung up **within `EarlyHangupWindow` (3 s) of ACCEPT** (`RemoteHangup`, `NodeLink.ConnectedDuration` below the window) | **Yes**, back-off `InitialDelay`×`Factor`^(n−1) capped at `MaxDelay`, up to `EarlyHangupMaxRetries` (30 ≈ 28 min; was 10 until 2026-11-06, when 27339 accepted only on retry 10); notice "Node N hung up straight after accepting; it probably doesn't know this node yet (a busy hub can take 10 minutes or more). Retrying in X s (retry n of 30)"; after the limit error "Node N keeps hanging up right after accepting the link…" and stop. Why: live-verified that an ASL3 node accepts, then app_rpt hangs up ~90 ms later ("Reported node N cannot be found") until its own node list has learned this node (minutes after registering or after an address change). Needs `AutoReconnect` on. The counter resets on a new user Connect and when a link stays up past the window. |
| Remote hung up later than that (`RemoteHangup`) **after sending `!!DISCONNECT!!`** (`Iax2Session.RemoteRequestedDisconnect`) | No — notice "Node N disconnected …: the far node ended the link on purpose" |
| Remote hung up later than that **without** `!!DISCONNECT!!` (its Asterisk restarted, or it gave up on packets we could not answer in a network gap shorter than our 45 s watchdog) | **Yes (2026-11-12)**, back-off `InitialDelay`×`Factor`^(n−1), up to `RemoteHangupMaxRetries` (5, app_rpt's own `MAX_RETRIES`) drops in a row; notice "Node N dropped the link … without asking to disconnect … Reconnecting in X s (try n of 5)"; a link that stayed up `StableLinkTime` (5 min) starts a fresh count, as does a user Connect; after the limit error "…has dropped the link n times in a row, so NodeX has stopped reconnecting" and the node is removed. Until 2026-11-12 this case was never redialled, which is how linked nodes "randomly" vanished (§5.6). |
| Remote rejected / busy | No — error |
| User pressed Disconnect | No (also cancels any reconnect cycle) |
| `AutoReconnect` off | No — error "Link to node N lost" |

Back-off (`ReconnectPolicy`): first retry after `InitialDelay` 2 s (immediately after a network change), ×`Factor` 2 per failure, capped at `MaxDelay` 60 s, each attempt must reach `Connected` within `AttemptTimeout` 12 s, `MaxAttempts` null (unlimited) by default. While there is no usable network the loop waits (re-checking every 5 s or on any network event) instead of burning attempts. Status is published as `ReconnectStatus(Active, Attempt, NextAttemptIn, Message)`.

**Rules added 2026-11-12 (after a phone log of six nodes all hanging up straight after accepting when Wi-Fi came and went within 9 s):**
- *A network change gives every node a fresh early-hangup budget.* Each new public address makes every far node hang up straight after accepting until it has learned that address, and a network event also cuts the back-off wait short, so a phone hopping between Wi-Fi and cellular spent the 30 retries far faster than the 28 minutes they were sized for. `HandleNetworkChangedAsync` zeroes `NodeConnection.EarlyHangups` on a real (signature) change.
- *A call's end is handled exactly once* (`NodeLink.EndHandled`). A link that dropped just as its reconnect loop finished was reported both by its state event and by the loop's last look, which could count two retries for one hang-up.
- *Every node that leaves the list logs why:* `[link] node N: no longer linked and will not be redialled: <reason>` (`RemoveConnection`). **Media commands never hang a node up (owner decision 2026-11-12):** in the Android car/media service (`NodeXMediaBrowserService`) a media **Pause or Stop** (car screen, steering wheel, headset or Bluetooth stereo) and a **permanent audio-focus loss** (another app took the audio) now **mute every linked node** and keep the links up; **Play** unmutes them (and re-requests audio focus). The mute is the driver's choice, so a later navigation prompt does not undo it. Before this they hung up the car's current node with nothing in the log, which looked like a link dropping by itself. Rules in Core `CarMediaButtons` (Pause/Stop → `Mute`, Play → `Unmute`; pinned by `AndroidServicePolicyTests`); the service side logs `[car] muting N node(s), none hung up: <why>`, `[car] unmuting …` and every `audio focus changed`. Hanging up from the car is still done with the per-node Disconnect / Disconnect all custom actions. Not tested on a car or phone.

**Network handoff:** `INetworkMonitor` (default `SystemNetworkMonitor`: `NetworkChange` + `NetworkInterface`) reports changes; events are **debounced 0.5 s** (1.5 s until 2026-11-06) and compared by *signature* (set of up, non-loopback, non-tunnel, non-link-local IPv4 addresses) so DHCP renewals are ignored. On a real change: (1) **re-register immediately** (so the registry/remote nodes learn the new address; waits up to 5 s), (2) if a link was established, end it and **re-dial immediately** from the new address (an IAX2 call is bound to the source address and cannot migrate), (3) if the network vanished, wait for it to return. Where the platform restricts network enumeration (recent Android) `SystemNetworkMonitor` degrades to "assume available, signature unknown" and the keep-alive watchdog still catches the loss (~45 s). Android uses `AndroidNetworkMonitor` (ConnectivityManager callback) instead.

### 4.7 Settings, secrets, registration, resolution
- **`NodeSettings`** (JSON, `%LOCALAPPDATA%\NodeX\settings.json`, atomic write, corrupt file kept as `.corrupt`): `Theme` (Dark/Light/HighContrast), `TextScale` (1.0–1.5), `NodeNumber`, `Callsign`, `ListenOnly` (default false), `DefaultTargetNode`, `FavoriteNodes`, `RegistrationUrl`, `IaxPort`, `RegisterOnStartup`, `AutoReconnect`, `InputDeviceId`/`OutputDeviceId` (device names chosen in the desktop pickers; ignored on Android/iOS), `MicGain`, `MicProcessing` (default true), `TimeOutTimerSeconds`, `TogglePtt`, `DefaultToSpeaker` (default false; Android only), `HardwarePttKey` (Android key code of a phone button used as PTT; 0 = none), `IdReminderEnabled` (default **false**), `IdReminderMinutes` (default 10, valid 1–60). **The password is never in this file.**
- **`NodeSettingsValidator`:** node number 3–7 digits; password required, ≤79 chars (ASL buffer is 80); callsign `[A-Za-z0-9]{1,6}(/[A-Za-z0-9]{1,6})?` and **required unless `ListenOnly`** (a callsign that is given is still checked in listen-only mode; the app does not verify it against any licence database); https registration URL; port 1–65535; mic gain 0.1–8; TOT 10–600 s; favourites must be node numbers. Normalisation trims, upper-cases the callsign, de-duplicates favourites.
- **Moving settings between devices (2026-11-06; unit-tested, not yet tried on a phone):** there is **no automatic sync**. Android Auto Backup (`backup_rules.xml`, `data_extraction_rules.xml`) only restores on the same phone or at new-phone setup, and `settings.json` never held the password anyway, so a second phone gets nothing from it. Instead Settings → "Move settings to another device" has **Export to file / Import from file** (Avalonia `StorageProvider`, no type filter because Android pickers mislabel .json) and **Copy / Paste clipboard**. `Core/Settings/SettingsTransfer`: JSON `{format:"nodex-settings", version:1, settings, secret?}`; audio device ids are never exported (device-specific); the password is included **only with a passphrase**, AES-256-GCM, key from PBKDF2-SHA256 (200 000 iterations, random salt and nonce), KDF iterations bounded on import. Import only **fills the form** (the user presses Save, so the normal validation applies); a file without a password leaves the on-screen one alone. Tests: `SettingsTransferTests`. Ideas not built: QR code, opt-in cloud-folder sync.
- **`NodeConfigService`:** validates, then writes the **password to `ISecretStore` first**, then settings; raises `Changed`. Invalid input writes nothing.
- **Secret storage per platform:** Windows **DPAPI** (`CurrentUser`); macOS **Keychain** (`/usr/bin/security`) and Linux **`libsecret`** (`secret-tool`), falling back to AES-GCM with a random key in a 0600 file on headless/minimal systems without a keyring daemon; Android **Keystore** AES-256-GCM key + ciphertext in private SharedPreferences; iOS **Keychain** with `AfterFirstUnlockThisDeviceOnly` (readable in the background with the screen locked, not migrated to backups/other devices). An unreadable secret is treated as "not stored" (user re-enters it).
- **`AslRegistrationService`:** see §5.1. Exposes `PerceivedAddress`, `PerceivedPort`, `ServerRefresh`; interval = configured (60 s) but never longer than the server's `refresh`. **Failure handling (2026-11-11):** a request that gets no verdict from the registry (no connection, a timeout, HTTP 5xx/408/429, a reply that is not JSON) is retried after `RetryDelay` (5 s, doubling, never longer than the interval) instead of a whole interval later, and while the node is registered **one** such request in a row is ridden out without reporting "not registered" (`ToleratedTransportFailures`; so `RegisteredSince`, the badge and the 15 s settle wait are not reset by a blip). A refusal from the registry itself (HTTP 4xx, or a reply without "successfully registered") is reported at once and retried at the normal interval. An out-of-band `SendHeartbeatAsync` (network change, resume) re-plans the loop's next beat, so a failed immediate registration is retried within seconds. An HTTP timeout is now a failed request; it used to escape as a cancellation and end the background loop for good.
- **`NodeResolution`:** static overrides → 15-minute cache → ASL stats REST lookup → DNS A record (`<n>.nodes.allstarlink.org`, then legacy `node<n>.allstarlink.org`; IPv4 preferred). Injectable HTTP/DNS/clock for tests. **`Invalidate(node)` (2026-11-11)** drops one cached address; `NodeRuntime` calls it when a dial goes unanswered (a first connect that gets no response, or a reconnect attempt that does not reach Connected), so a node whose public address changed is looked up again on the next try instead of being re-dialled at the old address for the rest of the 15 minutes. If every lookup then fails, the **last known address** is returned rather than nothing (not re-cached). Not invalidated when our own network changes.
- **`NodeDirectory` (Core/Services, 2026-11-06) — "Find a node" data source.** Uses the stats website's own list feed: `POST https://stats.allstarlink.org/api/stats/nodeList` (server-side DataTables; found in the page's `nodeList.js`, response shape verified live 2026-11-06). Form fields: `start`, `length`, `search[value]` (global, contains), `columns[i][search][value]` (per column, contains), `order[0][column|dir]`. Columns: 0 node (HTML link), 1 bubble, 2 callsign/owner, 3 "Freq" (free text: a frequency or a name), 4 tone, 5 location, 6 country, 7 site, 8 affiliation, 9 connections ("" = none). **Only columns 0, 2, 3, 7, 8 are sortable on the server** (the page marks 1, 4, 5, 6, 9 non-orderable), so "most connected" and sort-by-country are not offered. **It is not a documented public API:** it can change, and it returns HTTP 429 when hit too fast (seen once while probing). So: one request at a time, 2-minute cache of identical queries, 450 ms typing debounce in the UI, 50 rows per page, a `NodeX/1.0` User-Agent, failures reported as `NodeDirectoryException` with a screen-ready message. **Open item: ask AllStarLink (the page credits KK9ROB / WD6AWP) whether a supported search API exists or whether this use is acceptable.** Digits-only free text is routed to the node column. Test: `NodeDirectoryTests`, `NodeSearchViewModelTests`.

### 4.8 UI layer
**Find a node (2026-11-06, desktop-verified once, not on a phone):** `NodeSearchViewModel`/`NodeSearchView` full-screen overlay opened from "Find a node…" on the Connect card (designed in Flowstep first: file "NodeX UI Review", screen "NEW Find a node - phone"). One search box (type-as-you-go), one-tap country chips, "More filters" (country, frequency or name, callsign), sort (node number / callsign / frequency-name), result cards (node, callsign, description + tone, place, linked count, ★ favourite, **Use** which fills the connect box and connects immediately), "Show more" paging, friendly error with Try again. ★ saves via `NodeConfigService` (needs complete settings); `NodeRuntime` no longer re-registers when only non-registration settings change. **Phone button as PTT (Android, 2026-11-06):** `Core/HardwarePttMapper` maps one key code; Settings → "Phone button for push-to-talk" → **Choose button** captures the next key (Back/Home/Power/Recents/Menu are never captured), **Clear** removes it. `MainActivity.DispatchKeyEvent` feeds every key to it; the mapped key is consumed (so a volume key no longer changes volume) and drives `NodePttController.HandleButtonAsync`, so it honours hold vs Toggle PTT (Settings checkbox "Toggle PTT" and the Mode pill). **Limitation: works only while NodeX is on screen** (activity key events); the separate Android headset media-key path uses a foreground-service `MediaSession` for background Play/Pause PTT. Tests: `HardwarePttMapperTests`, `PttButtonTests`. **State/city filters:** the list has one free-text Location column (no state, county or city fields; ~half of US rows are "City, ST", others "Houston/Texas", "Las Vegas NV", a bare city, or junk). City → Location column filter. State → two server queries (the code and the full name, restricted to Country = United States, up to 2 x 200 rows each) merged, de-duplicated and re-checked by `UsStates.Matches` (token-aware: "West Virginia" is not Virginia, "Kansas City" is not Kansas, lower-case "or" is not Oregon), sorted/paged locally; a banner says it is best effort and when the result was truncated (live check 2026-11-06: TX 660 + Texas 359 raw matches). **County is not in the data and cannot be filtered.** Tests: `UsStateSearchTests`, `NodeSearchViewModelTests`. **Transmit-blocked UI:** PTT button disabled and relabelled "LISTEN ONLY"/"MIC OFF", amber banner with the reason and an **Allow microphone** (Android: system prompt, or app settings after "Don't ask again") or **Add callsign** button. iOS also feeds `MicrophoneAllowed`; its permission behavior is not device-tested.

- **Palette** (App.axaml, dark): `ConsoleBackgroundBrush #0D1117`, `CardBackgroundBrush #161B22`, `CardBorderBrush #30363D`, `HeaderBackgroundBrush #1F242C`, `TxRedBrush #EF4444`, `RxGreenBrush #10B981`, `AmberAccentBrush #F59E0B`, `CyanAccentBrush #06B6D4`, text `#F0F6FC` / `#8B949E` / muted `#848D97` (was `#484F58`, only 2.1:1 on cards; now 5.1:1 on cards, 4.6:1 on the header = WCAG AA), `DangerButtonBrush #DC2626` (fill for buttons carrying white text such as Disconnect: 4.8:1; `#EF4444` gives only 3.8:1). These are the dark defaults. `Appearance` supplies Light/HighContrast palettes; use `DynamicResource` and named brushes, not new hard-coded colours.
- **`MainView`:** header (branding, station callsign/node, ASL-registered badge, BLE connection badge, **Settings** button), transceiver card (state badge, latency, node display, RX/TX indicators, real VU meters, TOT bar), PTT button (press/hold handled in code-behind with tunnelled pointer events; toggle via click), connect card (node box, Connect/Disconnect, favourites), DTMF keypad + three function macros, event log. The **settings overlay** is a full-screen `Border` bound to `IsSettingsOpen` containing `SettingsView` — chosen over a separate window so it works on phones.
- **Responsive layout (`MainView.axaml.cs`):** one set of named cards (`HeroCard`, `ConnectCard`, `RxTxRow`, `MetersCard`, `PttCard`, `DtmfCard`, `LogCard`) is re-parented by width — **no separate mobile view, so bindings are shared**. Width < `NarrowBreakpoint` (720): single scrolling column ordered for operating (hero → connect → RX/TX → meters → keypad → log) and the **PTT card is moved into `PttDock`** (bottom row of the root grid, always visible, full-width 96 px button, thumb reach). Width ≥ 720: left column hero/RX-TX/meters/PTT, right column (360 px) connect/DTMF/log. **Height rule (`ShortHeightBreakpoint` 520):** a window shorter than 520 px also docks the PTT card (so a landscape phone, ~860×420, keeps PTT reachable while the two columns remain); in that case the PTT button is 64 px high, the PTT info text is hidden and the card padding shrinks. The header has its **own breakpoints** (`HeaderMode` enum): `Wide` ≥ 920; `Compact` < 920 (brand/station/Exit and badges); enlarged text adds a separate station row and scales the wide-header breakpoint; `Short` when also height < 520 (brand | station | Settings on one row, badges on a second, tagline hidden, to save vertical space). The root gets the `narrow` class (width < 720 only), which enforces ≥ 44 px button/text-box heights and larger keypad keys. `ApplyLayout(narrow, isShort)` derives `dock = narrow || isShort`. `MainWindow.MinWidth` is 340 so the phone layout can be exercised on desktop. **Gotcha:** when switching to fewer grid columns every child's `Grid.Column` must be ≤ the new column count (a stale index in a hidden child crashed Avalonia's grid measure).
- **PTT button face:** the `Button` is transparent and the visible face is the `PttFace` Border inside it (classes `keyed` toggled by `IsTransmitting`); styling the Fluent template presenter did not take effect, and this also prevents hover/press from overriding the red keyed state. The keyed state is covered by headless rendering checks; hardware transmit behavior is a separate test.
- **State is never colour-only:** the RX and TX cards show text ("Idle"/"Receiving", "Idle"/"ON AIR") in addition to colour, with `AutomationProperties.LiveSetting` (Polite for RX, Assertive for TX). The BLE dot is grey when not connected and cyan when `IsBleConnected`. The PTT-mode button is a visible bordered pill with a "⇄ tap to switch" hint. Interactive controls, meters and the settings text boxes carry `AutomationProperties.Name` for screen readers (**not yet tested with a real screen reader**). The tagline reads "AllStarLink Radioless Node".
- **Settings screen layout:** `SettingsView` root is `Grid RowDefinitions="*,Auto"`: the scrolling form is row 0; row 1 is a pinned action bar (error/status banners + Test registration / Cancel / Save) so the actions are always visible, including on short phone screens.
- **`MainViewModel`:** thin adapter. All state changes from Core threads are marshalled with `OnUi(...)` (`Dispatcher.UIThread`); audio levels are stored in volatile fields and applied by a 100 ms `DispatcherTimer`. Opens Settings automatically on first run. Reconnect status drives the badge ("RECONNECTING (n)") and keeps Disconnect enabled so the user can cancel.
- **DTMF macros:** `*3<node>` (link), `*1<node>` (unlink) using the Connect box's node, `*70` (status); digits sent one per 120 ms via `Session.SendDtmfAsync` — **to the transmit node's link only** (§4.5).
- **Phone bottom navigation (2026-11-06):** below `NarrowBreakpoint` the page is split into four tabs (`NavBar`, `Tab` enum in `MainView.axaml.cs`; the tab is not persisted). **Radio**: `IdReminderCard`, `CompactLinksCard` (slim rows: name, status/RTT, Receiving pill, red TRANSMIT tag or a Transmit here button, Mute/Muted with speaker glyph), `RxTxRow`, `MetersCard`, and the docked PTT (`PttDock` shows only on this tab). **Nodes**: `ConnectCard` + the full `LinksCard` (favourite star, disconnect). **Keypad**: `DtmfCard`. **Log**: `LogCard` (full height). The hero card and the header links strip (`HeaderLinksStrip`) are not shown on phones. On phones the PTT card holds only the key plus the Android phone/speaker chip beside it (`PttRow`, `SpeakerChipText`); the TOT bar and the toggle/hold pill are hidden (mode is the Settings checkbox). Wide and short-landscape layouts are unchanged. Headless rendering checks cover phone/desktop and enlarged text; real-device insets and keyboard behavior remain to check.
- **Linked nodes card (2026-11-06, `LinksCard`, `LinkItemViewModel`):** shown once anything is linked; header "Linked nodes" + "n / 8 linked" (follows `MaxLinks`); one row per node: "<node> · <favourite name>" (else "Node N"), callsign • description, text status (CONNECTED / CONNECTING… / RECONNECTING (n)) + "RTT n ms", a green **Receiving** pill when audio is arriving from that node, a red **TRANSMIT HERE** tag with a red left edge on the transmit node or a **Listen only** tag + **Transmit here** button on the others, **Mute / Muted · tap to hear**, and **Disconnect** (per node). **Connect** in the Connect card now adds a link (hint text; button stays enabled until `MaxLinks` are linked); its Disconnect button becomes **Disconnect all** when more than one is linked. The hero card shows the transmit node (and "also listening: …"), or "No transmit node" with a hint. The PTT key is inert (headline **CHOOSE TX NODE**, caption NO TX NODE, amber banner "Pick which linked node to transmit to.") when nodes are linked but none is chosen (`NeedsTransmitNode`/`CanKey`); with several nodes the caption reads "TO NODE n". RX card = any link receiving; speaker meter = loudest link; mic meter = transmit link. Log lines are tagged `[node]` when more than one is linked. Placement: narrow = after the hero card; wide = top of the right column. **Not seen on screen with real links.**
- **`SettingsViewModel`/`SettingsView`:** form for the fields in §4.7 + **Test registration** (really registers using the on-screen values; nothing saved) + Save/Cancel; errors listed as bullets; password masked with a Show toggle.

---

## 5. ASL & IAX2 protocol requirements (verified facts)

### 5.1 HTTPS registration (verified against `AllStarLink/app_rpt` `res/res_rpt_http_registrations.c`)
- `POST https://register.allstarlink.org/` — **root path**, `Content-Type: application/json`.
- Body: `{"port":4569,"data":{"nodes":{"<node>":{"node":"<node>","passwd":"<password>","remote":0}}}}` — `node` is a JSON **string**; field is `passwd`; `port` omitted if 0; `remote` is the integer 0 for a real node.
- Reply JSON: `ipaddr` (public address the registry sees), `port`, `refresh` (seconds), `data` (status text). **Registered iff `data` contains "successfully registered".**
- Official client re-registers every **60 s**. Registration also keeps the node's public address current in the ASL registry/DNS, which is how remote nodes can validate us (§5.4).

### 5.2 IAX2 frames (RFC 5456)
- Full frame header 12 bytes (F bit + 15-bit source call, R bit + 15-bit dest call, 32-bit timestamp, OSeqno, ISeqno, frame type, C bit + 7-bit subclass); mini frame 4 bytes (source call, 16-bit timestamp). Frame types: DTMF 1, Voice 2, Control 4, IAX 6, Text 7.
- **IAX subclasses (§8.4):** NEW 1, PING 2, PONG 3, ACK 4, HANGUP 5, REJECT 6, ACCEPT 7, AUTHREQ 8, AUTHREP 9, INVAL 0x0A, **LAGRQ 0x0B, LAGRP 0x0C**, REGREQ 0x0D, REGAUTH 0x0E, REGACK 0x0F, REGREJ 0x10, REGREL 0x11, VNAK 0x12, DPREQ 0x13, DPREP 0x14, DIAL 0x15, TXREQ 0x16, TXCNT 0x17, TXACC 0x18, TXREADY 0x19, TXREL 0x1A, TXREJ 0x1B, QUELCH 0x1C, UNQUELCH 0x1D, **POKE 0x1E**, MWI 0x20, UNSUPPORT 0x21, TRANSFER 0x22; **CALLTOKEN 0x28 is an Asterisk extension** (not in the RFC table; IE 54 / 0x36).
- **Control subclasses (§8.3):** Hangup 1, Ringing 3, **Answer 4, Busy 5**, Congestion 8, Flash 9, Option 0x0B, **Key Radio 0x0C, Unkey Radio 0x0D**, Call Progress 0x0E, Proceeding 0x0F, Hold 0x10, Unhold 0x11. (`AST_CONTROL_KEY`/`UNKEY` *are* the RFC's Key/Unkey Radio pair.)
- **Keep-alive:** `POKE` is only valid **outside** a call with destination call number 0 (§6.7.1). In-call liveness uses **PING/PONG** (§6.7.2; RFC default 20 s, NodeX uses 15 s for cellular-NAT headroom).
- **Codec:** µ-law (format bit 0x04) at 8 kHz, 20 ms = 160 bytes per frame.

### 5.3 Call handshake
`NEW`(+empty CALLTOKEN IE) → `CALLTOKEN`(token) → `NEW`(+token, fresh sequence space) → `ACCEPT` → we `ACK`. Then control/voice flow. Hang-up: `HANGUP` with a `CAUSE` IE.

### 5.4 How a node-to-node link logs in (from ASL `iax.conf` docs; **verified live against ASL3 node 523800**, see `docs/LIVE_VALIDATION.md`)
Incoming node connections land on the IAX user **`radio`** (`[radio]`: `type=user`, `context=radio-secure`, **no secret**); authentication happens afterwards in the `radio-secure` dialplan via `app_rpt`, which validates the calling node against the **remote node's own local node list** (refreshed from the registry/DNS on a delay of minutes — hence the importance of registration, and the early-hangup retry rule in §4.6). Therefore the link must use **Username `radio`**, `CALLING_NUMBER` = our node number, **no password**. The node **password is for registration only** and is never sent to remote nodes (a test asserts this). `[allstar-public]` (md5, secret `allstar`) and `[allstar-sys]` (RSA) are different, unused paths.

### 5.5 Keying and DTMF
PTT down → control `KEY` (0x0C) then voice; PTT up → stop voice, control `UNKEY` (0x0D). DTMF = full frame type 1 whose subclass is the ASCII digit. ASL function codes used: `*3<node>`, `*1<node>`, `*70`.

### 5.6 Deliberate disconnect vs dropped call (verified in the app_rpt source 2026-11-12; **not yet seen live**)
Read from `AllStarLink/app_rpt` `apps/app_rpt.c` (`remote_hangup_helper`, `handle_link_data`) and `apps/app_rpt/app_rpt.h`:
- A node that ends a link **on purpose** (operator unlink, a dialplan connection time limit) first sends the text frame **`!!DISCONNECT!!`** (`DISCSTR`; sent with its terminating NUL, `sizeof(DISCSTR)`), then hangs up. The receiving node stops its retries (`rpt_link_stop_retries`).
- A link whose far end hangs up **without** that text is treated as dropped: an outbound link that had connected is redialled, up to `MAX_RETRIES` = 5 (`MAX_RETRIES_PERM` for permanent links). No `!!DISCONNECT!!` is sent on an Asterisk shutdown or when the far side's own IAX2 retransmits time out.
- NodeX follows the same rule (§4.6): `Iax2Session.IsDisconnectText` matches the text exactly after trimming NUL/whitespace; full frames are processed in sequence order, so the text is always seen before the `HANGUP` that follows it.
- **Unverified:** that the nodes the owner links to (ASL3 and older app_rpt/HamVoIP builds) all send `!!DISCONNECT!!` on an operator unlink. If one does not, NodeX redials it up to 5 times after being unlinked on purpose, then stops.

---

## 6. Runtime behaviour reference (numbers in one place)

| Parameter | Value | Where |
|---|---|---|
| Keep-alive interval / misses before dead | 15 s / 3 | `Iax2Session.KeepAliveInterval`, `MaxMissedKeepAlives` |
| Retransmit timer / max | 1 s initial → 2×RTT (100 ms–10 s), ×2 per retry, cap 10 s / 4 retries | `Iax2Session`, `Iax2RetransmitQueue` |
| Registration interval | 60 s (≤ server `refresh`) | `AslRegistrationConfig` |
| Reconnect delay | 2 s ×2 … cap 60 s; attempt timeout 12 s; unlimited | `ReconnectPolicy` |
| Network-change debounce / registration wait | 0.5 s / 5 s | `ReconnectPolicy` |
| Wait after registering before a first dial | 15 s | `ReconnectPolicy.RegistrationSettleDelay` |
| Early-hangup window / retries | 3 s / 30 (about 28 min); the count restarts on a network change | `ReconnectPolicy`, `NodeRuntime.HandleNetworkChangedAsync` |
| Redials after an unrequested far-end hang-up / healthy-link time that resets the count | 5 in a row / 5 min | `ReconnectPolicy.RemoteHangupMaxRetries`, `StableLinkTime` |
| Registration retry after a lost request | 5 s, doubling, capped at the interval; 1 lost request tolerated while registered | `AslRegistrationConfig.RetryDelay`, `AslRegistrationService.ToleratedTransportFailures` |
| HANGUP acknowledgment wait / re-send | 300 ms / every 100 ms (0 on a network change) | `Iax2Session.HangupAckWait`, `HangupResendInterval` |
| Link-list text ("L ") interval | 10 s | `Iax2Session.LinkListInterval` |
| Max simultaneous links | 8 | `NodeRuntime.MaxLinks` |
| Jitter buffer | prime 3 frames (60 ms), capacity 25, conceal ≤3 | `JitterBuffer` |
| Device backlog cap | 200 ms | `NodeAudioEngineOptions` |
| TOT default | 180 s | `NodeSettings`, `NodePttController` |
| Resolution cache | 15 min | `NodeResolution` |
| Password max length | 79 | `NodeSettingsValidator` |

---

## 7. Mobile background execution

### Android (`NodeX.UI.Android`) — implemented; owner has used a physical phone
- `NodeForegroundService` follows `NodeRuntime.BackgroundChanged`: idle registration uses specialUse (API 34+) or connectedDevice; linked operation uses mediaPlayback/connectedDevice plus microphone when allowed. A refused microphone type falls back to listening. Locks are held only while linked. Unchanged types update the notification without restarting the service. These decisions live in tested `AndroidServicePlan`.
- **Bluetooth SCO** routing (`SetCommunicationDevice` on API ≥ 31, legacy `StartBluetoothSco` below) — switches audio mode to *in-communication* **only when a Bluetooth headset is actually present**, and **not while the loudspeaker is chosen** (2026-11-11: `NodeForegroundService.Apply` runs on every change, such as a second node linking or a settings save, and used to start SCO again, pulling the audio back to the headset in communication mode while the Speaker chip still said speaker; unverified on a phone).
- **Notifications keep their own sounds (2026-11-09).** In communication mode Android replaces other apps' notification sounds with a generic beep. The loudspeaker route (`AndroidAudioRoute.SetSpeaker(true)`) therefore stays in `Mode.Normal` and the `AudioTrack` uses `Media` usage (rebuilt when the route changes); only the phone-earpiece route and a Bluetooth headset still use communication mode. **Not verified on a phone** (whether the generic beep is gone, and that speaker audio and echo are still fine).
- `MainActivity` requests `RECORD_AUDIO`, `POST_NOTIFICATIONS` (API ≥ 33), `BLUETOOTH_CONNECT` (API ≥ 31) at startup. Manifest declares all required permissions and foreground-service permissions. `MainActivity.OnResume` asks **once** (pref `battery_exemption_asked`) for the battery-optimisation exemption, only after a link is up (so it appears on the next resume after the first connect). **`AndroidNetworkMonitor`** (`ConnectivityManager.RegisterDefaultNetworkCallback`, event-driven, signature = network handle + IPv4 addresses) replaces `SystemNetworkMonitor` on Android. **Battery model:** mic closed unless keyed; UI meter timer and the 1 s "last heard" age timer stopped while backgrounded (ages are refreshed on return).
- Audio: `AudioRecord`/`AudioTrack`, 8 kHz mono, `VoiceCommunication` source/usage (platform echo cancellation); non-blocking writes; `BufferedPlayback` from playback head position.
- Use `OperatingSystem.IsAndroidVersionAtLeast(n)` guards (the CA1416 analyzer understands them; `Build.VERSION.SdkInt` comparisons produce warnings).

### iOS (`NodeX.UI.iOS`) — implemented, not device-tested
- `Info.plist`: `UIBackgroundModes` = `audio` + `bluetooth-central` (restored with `IosBlePttTransport`); version 1.15 (17), kept in step with Android; the version is passed to `AppServices.Initialize` from the bundle; microphone and Bluetooth usage strings. `AppDelegate` also wires `AVAudioSession.RecordPermission` into `Transmit.MicrophoneAllowed` / `AppServices.MicrophonePermissionRequester`, `UNUserNotificationCenter` into `AppServices.IdReminderNotifier`, and `UIImpactFeedbackGenerator` into `AppServices.HapticFeedback`.
- `IosAudioSessionManager`: `PlayAndRecord`, options `AllowBluetooth` (**this is the HFP option; the .NET binding has no `AllowBluetoothHfp` name**) | `MixWithOthers` | `DefaultToSpeaker`, mode `VoiceChat`, preferred 8 kHz / 20 ms buffer; observes interruptions and route changes. One shared instance (it registers observers).
- `IosAudioDeviceService`: `AVAudioEngine`; input tap at the hardware rate → `PcmResampler` → 8 kHz PCM16; playback via `AVAudioPlayerNode` (8 kHz float buffers; the engine resamples). Rebuilds the engine after an interruption ends. Use `IosAudioDeviceService.RecommendedEngineOptions` (zero-PCM keeper) when registering services.

---

## 8. Bluetooth hardware and PTT — transports implemented; real-button checks remain
- **Audio:** OS Hands-Free Profile (HFP/SCO) appears as an ordinary mic/speaker route (Android SCO handled above; iOS via `AllowBluetooth`).
- **BLE PTT button (Android, 2026-11-09).** `AndroidBlePttTransport` (scan + paired devices, GATT connect, subscribe to every notifying characteristic, raw bytes out) implements `IBlePttTransport`; `BlePttManager` (Core, shared) keeps the saved button connected (retry with back-off), turns notifications into pressed/released, and runs the Learn wizard (`BlePttLearner`). `AppServices.BlePtt` is the process-wide instance; `MainViewModel` keys **only the transmit link** (`PttSource.BluetoothBle`, follows Momentary/Toggle).
- **Safety rules:** only the exact learned press/release packets on the learned characteristic count as PTT (no auto-detect keying; `BluetoothPttPacketParser` is unused by this path); the Battery Level characteristic is never PTT; a button with no release packet is a tap (pressed then released at once); a dropped connection always reports released and `MainViewModel` force-unkeys anything the button keyed (Toggle mode ignores a plain release); learning never keys.
- **Settings:** `BlePttAddress/Name/Characteristic/Press/Release` (`NodeSettings.GetBlePttProfile()`); Settings has Scan, pick, "Set up button" (press and hold, release), Forget. The header badge shows connection and pressed state. Permissions: `BLUETOOTH_SCAN`/`BLUETOOTH_CONNECT` (API 31+), `ACCESS_FINE_LOCATION` (API ≤ 30), requested at start-up.
- **Headset media-key PTT (Android, 2026-11-09, off by default, setting `MediaKeyPtt`).** While linked, an active `MediaSession` held by `NodeForegroundService` (`AndroidHeadsetPtt`) receives the headset's Play/Pause/Headset-hook button and toggles the transmit link via `MediaKeyTransmit` (rules and tests in Core: toggle only, keys only the connected transmit link, listen-only and no-mic refuse with a buzz, unkey always allowed). Beep on key, lower beep on unkey, buzz on refusal. It unkeys on `ACTION_AUDIO_BECOMING_NOISY` (headset unplugged), when the session stops or the link ends, and it stands aside while an Android Auto session is up. **Unverified on any headset:** whether Android routes the button to this session with the screen off, and what happens alongside a running music app.
- **Desktop BLE:** implemented with native bindings and the shared Learn wizard (§19); fake-driver tests cover readiness, disconnect, stale notifications and cleanup. Real-button tests remain outstanding.

## 9. UI specification and remaining checks

- Settings → Appearance provides saved Dark, Light and High contrast themes and 100–150% text size. Palette and typography use dynamic resources across Main, Settings, Find-a-node and the desktop window. Larger text uses a separate station header row; short-screen PTT height grows to keep its labels visible.
- Settings → Permissions and privacy explains microphone, Bluetooth, notifications and battery exemption. Android explains permissions before its system prompt, supports Not now and later Review permissions, and sends permanently denied requests to app settings. iOS provides a review action and the shared explanations; OS prompts remain platform-controlled.
- Headless Skia renders cover desktop, portrait phone and short landscape layouts with enlarged text. They do not validate physical-device safe areas, Android gesture bars, soft keyboards, screen readers or OS permission dialogs.
- Check station-ID notifications, settings transfer and desktop device selection on hardware. The owner has already reported successful Android background audio/handoff and DHU use; do not label those as wholly untested.
- Desktop has a headset/echo advisory. Software echo cancellation remains optional.

---

## 10. Security & privacy requirements
- The node password lives **only** in `ISecretStore` and in memory; never in settings JSON, logs, status messages, UI error text, or any IAX packet (tests assert each of these).
- Registration uses HTTPS only (validator rejects non-https URLs).
- A link never answers an IAX2 `AUTHREQ` with the password itself: only the MD5 challenge method is used, and a request that could only be met in plaintext (no challenge, or no password, which is the normal case for a node link) ends the call as rejected (tested).
- Only the dialled endpoint may send to the call socket; spoofed/other-call packets are dropped (tested).
- No telemetry. The only outbound traffic: registry (HTTPS), directory/DNS lookups, and IAX2 to the linked node.

---

## 11. Testing

`tests/NodeX.Core.Tests` (xUnit v2, **692 tests**, ~15 s; the runtime fixtures set `RegistrationSettleDelay` to zero, without it the suite takes about 4 minutes). It references Core **and** `NodeX.UI` (view-model tests). `tests/NodeX.UI.Tests` adds a separate xUnit v3 Avalonia Headless/Skia host for `MainViewModel` integration and rendering tests. Keep it separate from the Core project's xUnit v2 dependencies.

| File | Covers |
|---|---|
| `G711UlawTests`, `PcmResamplerTests`, `AudioTests` | codec, resampler, framer, level meter, jitter buffer, `PlaybackRingBuffer` (upsampler + 1 s cap), audio engine (fake device/channel) |
| `Iax2ProtocolTests`, `Iax2RfcConformanceTests`, `AslTextParserTests` | wire format; **every IAX/control subclass value vs RFC 5456**; ASL `L <links>`, `STATUS`, and `!NEWKEY1!` text parsing |
| `Iax2ReliabilityUnitTests`, `Iax2ReliabilityTests` | mod-256 rules, retransmit queue, VNAK, duplicates, wrap past 255, call-token handshake, sender/call-number validation |
| `Iax2KeepAliveTests` | PING/PONG, RTT, watchdog, silent-peer detection |
| `NodePttControllerTests`, `NodeResolutionTests`, `AslRegistrationServiceTests` | PTT states/TOT, resolution/cache/DNS, registration wire format and parsing |
| `SettingsTests`, `SettingsViewModelTests`, `AppearanceSettingsTests` | validation, JSON store, secret separation, desktop pickers, appearance persistence/transfer |
| `DesktopBlePttTransportTests`, `AndroidServicePolicyTests` | BLE readiness/disconnect/stale callbacks/cancellation/disposal; Android foreground/routing/media-button decisions |
| `Iax2SessionRobustnessTests`, `RxActivityTests` | throwing listeners, frames after the call ended, single-use, mid-call CALLTOKEN, HANGUP re-send and acknowledgment, MD5-only authentication, dialling a closed port, concurrent outbound/inbound sequence lock, preferred local port fallback, stateful `RxActivity` |
| `NodeRuntimeMultiLinkTests` | several nodes at once, transmit/mute rules, hang-up during a pending connect, re-lookup after a node moved address |
| `NodeRuntimeTests`, `NodeRuntimeReconnectTests` | runtime wiring (registration, login as `radio`, audio start/stop, teardown); **auto-reconnect, back-off, give-up, user cancel, network handoff, offline wait** |

**Test infrastructure:** `FakeIaxRemote` (loopback UDP "node" that tracks sequence numbers like a real peer, can send raw/duplicate/future/spoofed frames, auto-resets on a new call), `FakeAudioDevice`, `FakeNetworkMonitor` (switch/offline/raise), stub `HttpMessageHandler`s. Production timing is shortened through injectable properties (`KeepAliveInterval`, `InitialRetransmitTimeout`, `ReconnectPolicy`, `Action<Iax2Session> configureSession`).

**Rules for writing tests here:**
- Use `new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)` for anything signalled from a Core event; otherwise the test continuation runs *on the receive thread* and disposing the session from the test deadlocks.
- Wait on real state, not flags that flip early (e.g. wait for the *device* to be capturing; `NodeAudioEngine.IsRunning` becomes true before the device opens — this caused a flaky test).
- xUnit discovers only **public** test classes, so test methods can't take internal types as parameters (pass strings).
- Run the suite repeatedly (`for i in 1..10`) after touching timing code; it has been stable (6–12 consecutive clean runs after each major change).

---

## 12. Engineering conventions & known pitfalls (for any agent)

- **.NET tooling only.** Do not use Python (or other languages) to edit/patch this repo; use normal edits and `dotnet` commands.
- **Do not edit generated or output files; do not put `bin`/`obj` in the repo** (§3.3).
- **CommunityToolkit.Mvvm generates `On<Property>Changed` partial hooks.** Naming your own method e.g. `OnSessionStateChanged` for a property `SessionState` causes a duplicate-member compile error — use `Handle…`.
- **Avalonia 12:** `TextBox.Watermark` is obsolete → `PlaceholderText`. `InputMethodOptions` attached property does not exist as I tried to use it. Use the palette brushes.
- **iOS project has no implicit usings** (add `using System;` etc.). The .NET iOS binding names differ from Apple's (`AllowBluetooth`, `AVAudioSessionMode.VoiceChat`).
- **Android:** the file-scoped namespace `NodeX.UI.Android` shadows the global `Android` root — write `global::Android.Manifest…` / `global::Android.Net.WifiMode` inside it; `Android.Media.AudioFormat` collides with `NodeX.Core.Audio.AudioFormat` (use an alias).
- **Event threading:** Core events fire on network/audio threads. UI code must marshal to the UI thread; Core code must not dispose a session/link from inside its own handlers (use `Task.Run`).
- **Core must stay UI-free.** New cross-platform behaviour goes in `NodeX.Core` behind an interface (`IAudioDeviceService`, `ISecretStore`, `INetworkMonitor`, `IBluetoothPttService`), implemented in the heads and unit-testable with fakes.
- **Nothing sensitive in logs/UI strings.** Never include the password in any message.
- **State your verification honestly** in commit messages/PRs: "unit-tested", "builds", "ran on device X".

---

## 13. Current backlog

0. **"Nodes randomly dropping" (owner report 2026-11-12), open:** the fixes in §4.6/§5.6 are unit-tested only and the phone cause is unconfirmed. Needed: a phone build with them, then the **whole** log file the next time a node vanishes (§18 says what to search for). (Pause/Stop and audio-focus loss now mute instead of hanging up, §4.6; also unverified in a car.)
1. **Hardware validation:** real BLE PTT buttons/headset media keys (including disconnect/unkey and screen-off reconnect); full Linux/macOS app with audible TX/RX, selected devices, keyring storage and the Mac bundle; iPhone behavior if iOS is resumed.
2. **Device UI and permission checks:** safe-area/gesture insets, keyboards, screen readers, new themes/text sizes, Android permission dialogs, mobile ID notifications and settings transfer. These are implemented features awaiting device validation, not missing implementations.
3. **Car verification details:** owner reports the Desktop Head Unit working great. Record explicit results for voice search, cold start, focus interruptions, routing and Play listen-only behavior when exercised. A physical-car pass remains separate from the DHU report.
4. **Wider-release preparation:** finish the store listing, declarations, policy URL, data-safety/content/target-audience forms, real-device/car screenshots, signing verification and release smoke tests. Drafts/assets exist. See `docs/RELEASING.md` and `docs/PLAY_STORE_LISTING.md`.
5. **Directory API:** ask AllStarLink whether a supported node-search API exists or this use of the website feed is acceptable (§4.7). No contact has been sent.
6. **Remaining test boundaries:** shared Android service policies and MainViewModel behavior are tested; native Android service lifecycle/permissions/audio focus, native BLE drivers and OS keyrings still require platform tests. CI now runs the headless UI suite too; a local pass does not prove a hosted CI run succeeded.
7. **Optional enhancements:** DTMF BEGIN/END duration, IPv6, additional codecs and inbound mode over VPN. iOS release and idle background registration are intentionally parked (§20).

**Completed:** desktop BLE transport; theme/text-size controls; permission explanations; mobile ID notification hooks; iOS BLE/microphone wiring; ASL text parsing; desktop pickers/ring-buffer tests; OS keyring integration; macOS bundle scaffolding; headset advisory; `.editorconfig`; Android compile jobs. Implementation does not imply hardware verification.

**Ports are not a connection bug:** outbound calls deliberately use an available local UDP source port. Replies return to it. `IaxPort` is the port advertised for registration; `Iax2Session` also exposes an optional preferred local bind port for callers that need it, but normal `NodeLink` calls do not use it. Remote destination ports are independently resolved via stats/DNS/probing, including nonstandard ports. Wiring a fixed local bind is not required for the owner's working outbound connections and is not an actionable backlog item.

---

## 14. Unverified assumptions & risks (check before relying)

**Verified live against node 523800 on 2026-11-05** (details in `docs/LIVE_VALIDATION.md`): ASL registration; call-token handshake (empty CALLTOKEN IE on the first `NEW`); link login as `radio` with no password and `CALLING_NUMBER` = our node; keep-alive both ways; keying (`KEY`/`UNKEY` + voice) relayed on air; DTMF function codes (`*70`); real Windows mic → G.711 → network; Windows speaker output; re-dial from the same IP while a stale call exists. The owner subsequently reported good on-air voice-cleanup quality (§2). Instrumented audio comparisons and the specific risks listed below remain separate checks.

- **`NodeResolution` REST schema — VERIFIED & FIXED 2026-11-05 against the live service.** The inherited guess (`/api/node/<n>` → `data.ip`) was wrong (404). Real endpoint: `GET https://stats.allstarlink.org/api/stats/<n>` (docs: allstarlink.github.io/developers/api; 30 req/min/IP, cache results). The node's own record is the **top-level `node` object** (`ipaddr`, `port`, `callsign`, `node_frequency`, `Status`, ...); `stats.data.linkedNodes[]` describes its links and must not be used. Checked with node 2530 → 144.202.112.210:4569, identical to DNS `2530.nodes.allstarlink.org` A record (also verified live). Legacy `node<n>.allstarlink.org` returned no data (kept as a harmless last fallback). Nodes that don't report stats return 404 and fall through to DNS.

### Live validation

The dated results of testing against real nodes and hardware (registration, link, keying, DTMF, Windows audio, re-dial) are in [`docs/LIVE_VALIDATION.md`](docs/LIVE_VALIDATION.md). The console harness is `tools/NodeX.LiveCheck` (see §3.1 and that file). Summary of what is **verified** vs **not** is in §2 and the list below.

**Still unverified / open risks:**
- Native BLE buttons/headsets on every platform; full Linux/macOS app and real iPhone operation.
- Measured handoff delay/retry-budget sufficiency across different public IPs. The owner already reports handoff working; instrumentation would quantify it.
- New permission rationale dialogs and station-ID notification delivery on phones.
- Other app_rpt versions/configurations, real remote speech under packet loss, and optional DTMF BEGIN/END refinement. ASL status/link text is already parsed and displayed.
- Physical-car/audio-focus/voice-search cases not itemized in the owner's successful DHU report.

---

## 15. References
- IAX2: RFC 5456 — https://www.rfc-editor.org/rfc/rfc5456.txt (§6.7 keep-alive/lag, §7 reliability, §8.3/8.4 subclass tables).
- ASL3 HTTPS registration source — https://raw.githubusercontent.com/AllStarLink/app_rpt/master/res/res_rpt_http_registrations.c
- ASL `iax.conf` documentation (`[radio]`, `[allstar-public]`, `[allstar-sys]`) — https://allstarlink.github.io/config/iax_conf
- ASL HTTP registration overview — https://allstarlink.github.io/adv-topics/httpreg
- ASL3 project — https://github.com/AllStarLink/ASL3

---

## 16. Change log

Implementation history is in [`CHANGELOG.md`](CHANGELOG.md). Superseded proposals and dated platform investigations are retained in [`docs/HISTORY.md`](docs/HISTORY.md); they are not open requirements.

## 17. Amateur-radio behavior

- A callsign is required unless Listen only is selected. NodeX does not verify callsigns; README, Settings and the draft store listing explain the user's responsibility.
- The optional station-ID reminder is off by default. Its clock starts when a transmission starts and stays due until acknowledged or the links end. Acknowledging while transmitting starts a fresh clock. Android and iOS notification hooks are implemented; real-device notification delivery still needs checking.
- NodeX never generates an on-air ID or keys automatically. The operator identifies by voice.
- Rejected/busy links are surfaced. Early post-ACCEPT hangups retry at most 30 times with back-off capped at 60 seconds.
- Live tests that key RF require the owner's knowledge; see `docs/LIVE_VALIDATION.md`.

## 18. Diagnostics

`DiagnosticLog` writes a rolling local `nodex.log` plus two older files (512 KB each). Passwords/tokens are redacted; node numbers, callsigns and IPs can appear. Detailed logging is optional. Settings → Send logs opens Android's share sheet or the desktop file manager; nothing uploads automatically.

**"A node disappeared" (2026-11-12):** search the log for `no longer linked and will not be redialled` (the reason follows), `[car] muting` / `[car] unmuting` and `audio focus changed`. A pasted excerpt is not enough when it starts after the event: ask for the whole file from Send logs.

The receive highlight rule lives in Core `RxActivity` and is shared by the phone UI and car service. KEY-aware nodes follow KEY/UNKEY; non-KEY-aware nodes use a 500 ms voice window. A KEY without subsequent frames expires after 3 seconds. A lost UNKEY while a KEY-aware peer continues streaming remains an open limitation.

## 19. Desktop audio and Bluetooth

- Windows audio uses NAudio; Linux/macOS use PortAudio. Saved input/output names are resolved on each new link, with a logged fallback when a device is unplugged. Linux needs JACK/ALSA dependencies; a native-load failure falls back to silent audio.
- A small linux-arm64 harness ran on the owner's Raspberry Pi. It verified native loading, device listing, capture/playback opening and sample cadence. The Pi had no microphone and no listening check was performed. The full Avalonia app, real audio and keyring behavior still need Linux/macOS testing.
- `tools/package-macos.sh` builds a `.app` with microphone/Bluetooth usage descriptions. Packaging is implemented but has not been run on a Mac; signing/notarization is not automated.
- Desktop BLE uses `DesktopBlePttTransport` and `SimpleBleCentral` (SimpleSharpBLE 1.4.0). Supported native builds: Windows 10+ and glibc Linux, x64/ARM64; macOS 15+ on Apple Silicon. Intel Macs and older macOS do not have BLE support from this package. Linux needs BlueZ/D-Bus and permission to access the Bluetooth service. Native access is deferred until Scan/connect, so an absent adapter does not prevent ordinary operation.
- Scan reports paired and advertising devices; connection subscribes to available notification/indication characteristics. The existing Learn wizard records the exact button packets. Connecting/scanning/learning never keys; disconnect releases a held button and old-connection notifications are ignored. Native Windows adapter enumeration and scan/start/stop passed on the development PC. Real-button behavior remains unverified. See `THIRD_PARTY_NOTICES.md` for the separate BLE dependency license.

## 20. Intentionally deferred work

- iOS release remains parked: no Mac/signing account is available. Audio, BLE, safe areas and background longevity need an iPhone. See `docs/IOS_RELEASE.md`.
- Idle iOS background registration is not implemented. Registration resumes when the app returns. No silent-audio idle workaround or push server is planned without a separate decision.
- A cloud relay, inbound VPN mode, IPv6 and additional codecs are optional future directions, not blockers for outbound use.
- DTMF end-only frames already work for ASL function codes; BEGIN/END duration is optional polish.
- Long-lived HTTP clients retain their platform default handler. Replacing Android's native handler to set `PooledConnectionLifetime` was deliberately deferred; it is not a completed change.
