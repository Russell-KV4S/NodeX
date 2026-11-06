# NodeX — Project Specification & Engineering Guide
### Cross-platform, Asterisk-free AllStarLink node (.NET 10 / Avalonia UI)

> **Last updated:** 2026-11-05 · **Build:** `dotnet build NodeX.sln` → 0 errors, 0 warnings · **Tests:** 343 passing (xUnit)
>
> This document is written for **humans and AI coding agents** who pick the project up cold. Read §0 first.

---

## 0. How to use this document (read first)

1. **Authority order when sources disagree:** (1) the code and its tests, (2) this document, (3) anything else. An earlier "master specification" file existed and contained **wrong protocol details** (wrong registration endpoint, wrong IAX subclass numbers, wrong keep-alive); it has been removed from the folder and must not be recreated or trusted. Protocol facts below are verified against RFC 5456 and the ASL3 source — see §5 and §15.
2. **Be honest about verification.** The core protocol path has been validated live against a real ASL3 node (Windows only; log in `docs/LIVE_VALIDATION.md`), but **nothing has run on a *physical* phone, on Linux/macOS, or against a node on a different public IP**. §2 and §14 list exactly what is and is not verified. Do not describe unverified behaviour as working.
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
| **Multiple nodes at once** (up to 4; one transmit node, the rest listen-only; per-node mute) | Implemented 2026-11-06 (§4.5, §4.8). **Never run against live nodes or on a phone**; UI laid out in Flowstep ("NEW Multi-node links - phone") and only smoke-launched with nothing linked (the Linked-nodes card is hidden until a link exists, so it has not been seen on screen) | 13 new tests (`NodeRuntimeMultiLinkTests`, engine mute tests) with several fake nodes; solution builds incl. Android/iOS |
| Desktop head (Windows) | Runs; NAudio `WaveIn`/`WaveOut` audio; mic and speaker **verified** via `LiveCheck` (the full GUI app has not been used for an on-air QSO) | Launched and screenshot-checked; live audio checks |
| Desktop head (Linux/macOS) | Compiles; **runs silently** (no audio backend) | — |
| Android head | Builds; **shipped to Play internal testing (§3.5, 2026-11-05) and installed on the owner's phone; owner reported 1.1 "worked great" (2026-11-06, general verdict only; specifics such as screen-off background audio, Bluetooth and handoff are not itemised and remain §13 item 7)** | Build; Play internal test |
| iOS head | Compiles/builds; **never run on a device** | Build only |
| Bluetooth PTT (BLE GATT, SCO audio, media key, Learn wizard) | **Not implemented** (interface + packet parser only) | Parser tests |
| Responsive/phone layout | Implemented (single-column phone layout with docked PTT bar below 720 px or below 520 px height; two-column desktop above). **Verified only by resizing the Windows app (340–1100 px wide, 860×420 landscape), not on a real phone** | Screenshots at 340 / 410 / 780 / 1100 px and 1050×760, 800×760, 420×860, 860×420 |
| Live-node interoperability | **Verified against owner's cloud node 523800 (2026-11-05):** registration, call-token handshake, `radio` login, keep-alive, keying, DTMF `*70`, mic audio TX, re-dial (same IP). **Not verified:** different-IP handoff, TX audio quality by ear, other app_rpt versions | `tools/NodeX.LiveCheck`; log in `docs/LIVE_VALIDATION.md` |
| Optional station-ID reminder (§17.1) | Implemented on all heads' shared UI (prompt in the hero card + activity-log line); **mobile background notification not done**; screen not visually checked | Unit tests (`IdReminderTests`), solution builds |
| Transmit voice clean-up (`MicProcessor`) | Implemented, unit-tested; **not yet A/B-confirmed by ear on air** | Tests |

---

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
│   │   │   ├── AslRegistrationService.cs  # HTTPS registration (ASL3 wire format)
│   │   │   └── NodeResolution.cs          # node number -> IP: static overrides, REST, DNS fallback
│   │   ├── Audio/
│   │   │   ├── IAudioDeviceService.cs   # platform audio contract (8 kHz mono PCM16)
│   │   │   ├── IVoiceChannel.cs         # what the engine needs from a call
│   │   │   ├── NodeAudioEngine.cs       # capture->µ-law->net, net->jitter->playout->device
│   │   │   ├── JitterBuffer.cs  PcmFramer.cs  PcmResampler.cs  AudioLevelMeter.cs
│   │   │   └── NullAudioDeviceService.cs  # silent fallback / designer
│   │   ├── Link/
│   │   │   ├── NodeLink.cs              # ONE call = Session + Ptt + Audio (single-use)
│   │   │   ├── NodeRuntime.cs           # config + registration + active link + reconnect + handoff
│   │   │   ├── NetworkMonitor.cs        # INetworkMonitor + SystemNetworkMonitor
│   │   │   └── ReconnectPolicy.cs       # timing + ReconnectStatus
│   │   ├── Settings/
│   │   │   ├── NodeSettings.cs  NodeSettingsValidator.cs  Stores.cs  NodeConfigService.cs
│   │   ├── Bluetooth/IBluetoothPttService.cs  BluetoothPttPacketParser.cs   # contract + parser only
│   │   ├── NodePttController.cs         # PTT state machine
│   │   └── IdReminder.cs                # optional "time to identify" reminder (UI prompt only; transmits nothing)
│   └── NodeX.UI/                    # own Directory.Packages.props (central package versions)
│       ├── NodeX.UI/                    # shared Avalonia views + view models
│       │   ├── AppServices.cs               # composition root; heads call Initialize(...)
│       │   ├── App.axaml(.cs)               # palette (brushes), startup, orderly shutdown
│       │   ├── ViewModels/MainViewModel.cs  SettingsViewModel.cs  ViewModelBase.cs
│       │   └── Views/MainView.axaml  SettingsView.axaml  MainWindow.axaml
│       ├── NodeX.UI.Desktop/            # Program.cs, Audio/NAudioDeviceService, Security/ProtectedFileSecretStore
│       ├── NodeX.UI.Android/            # Application, MainActivity, Audio/, Security/, Services/ (foreground svc, locks, SCO)
│       ├── NodeX.UI.iOS/                # AppDelegate, Audio/ (session manager + AVAudioEngine device), Security/ (Keychain)
│       └── NodeX.UI.Browser/            # UNUSED template leftover; not part of the product (safe to delete)
└── tests/NodeX.Core.Tests/          # xUnit; references Core AND NodeX.UI (for view-model tests)
```

### 3.2 Targets and tooling
- `net10.0` (Core, UI, Desktop, tests), `net10.0-android` (**minSdk 26**, Android 8.0), `net10.0-ios` (min iOS 13).
- All projects are SDK-style. SDK in use: **.NET 10.0.403** (a 6.0 SDK is also installed; there is **no `global.json`** — adding one to pin 10.x would be sensible).
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
- **Identity:** `ApplicationId` = **`com.kv4s.nodex`** (permanent once uploaded; the foreground service and its intent actions use the same prefix). Play Console app **NodeX** under the developer account **RT App Works**; the app is free (a free app cannot later become paid). Donations, if ever added, would need a policy check first (Play Billing tip jar vs external link is unconfirmed).
- **Format and signing:** Release builds produce an **`.aab`** (Debug stays `.apk`). `NodeX.UI.Android.csproj` imports **`keystore/signing.props`** for Release; `keystore/` is **gitignored** and holds `nodex-upload.keystore` (alias `nodex-upload`, ~27-year validity, upload key under Play App Signing) plus the passwords in `signing.props`. **Back the folder up; it is the only copy.** Never commit it.
- **Build:** bump **`ApplicationVersion`** (versionCode, must increase every upload) and `ApplicationDisplayVersion` in the csproj, then `dotnet publish src/NodeX.UI/NodeX.UI.Android -c Release`. Output: `%LOCALAPPDATA%\NodeX\build\bin\NodeX.UI.Android\Release\net10.0-android\com.kv4s.nodex-Signed.aab` (~45 MB). Copy it to `keystore/nodex-<ver>-code<N>.aab` and check with `jarsigner -verify`.
- **Upload is manual:** the Play Console file picker cannot be driven by an agent (the browser upload tool caps files at 10 MB). The owner uploads the `.aab` under Testing → Internal testing → Create new release; an agent can fill the release name and notes (each `<en-US>` tag must be on its own line) and publish.
- **Testers:** email list **NodeX testers** (owner only, saved as the track's list). The shared **Honey and Blush testers** list belongs to another app and must not be ticked here. Opt-in link: `https://play.google.com/apps/internaltest/4699734114435882711`. The desktop store page says "Not Found" for a test-only app; install on the phone. The first release took a while to appear.
- **Releases so far:** 1.0 (code 1) and **1.1 (code 2)**, 2026-11-05 (listen-only, required callsign, Find-a-node, stay-registered + Exit, phone-button PTT, Toggle PTT). Play's two warnings (no deobfuscation file, no native debug symbols) are harmless: minification is off.
- **Still needed before any wider track:** privacy policy URL, data-safety form, content rating, target audience, store listing, and **foreground-service declarations** (microphone, media playback, connected device, special use) with justifications. A newer personal developer account may also need a closed test (about 12 testers for 14 days) before production.

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
        └─ one NodeConnection per wanted node (max 4) ─► per call: NodeLink = Iax2Session + NodePttController + NodeAudioEngine(+ IAudioDeviceService)
```
`NodeX.Core` has **no** dependency on Avalonia or any platform API. Platform heads implement `IAudioDeviceService` and `ISecretStore` and register them via `AppServices.Initialize(secrets, audioFactory, audioOptions)` **before** Avalonia starts (`Program.Main` / `Application.CustomizeAppBuilder` / `AppDelegate.CustomizeAppBuilder`).

### 4.2 `Iax2Session` (Core/Protocol) — one call over UDP
Single-use: it owns a socket and three background loops (receive, keep-alive, retransmit). **Create a new session per call; never reconnect a used one.**
- **Handshake:** first `NEW` carries an *empty* CALLTOKEN IE; on a `CALLTOKEN` reply it clears pending frames, resets sequence numbers/remote call number and re-sends `NEW` with the token; `ACCEPT` → `Connected`. `AUTHREQ`/MD5 auth is implemented (not needed for node links).
- **Reliability (RFC 5456 §7):** every full frame except ACK/INVAL/TXCNT/TXACC/VNAK/CALLTOKEN is sequenced (8-bit, wraps) and queued until the peer's ISeqno acknowledges it; retransmitted with the R bit on a timer (initial `InitialRetransmitTimeout` 1 s until an RTT is measured, then 2×RTT clamped 100 ms–10 s; doubles per retry, cap 10 s); after `MaxRetransmits` (4) the call is declared lost. Inbound: out-of-order (future) frames are ignored and answered with **VNAK**; duplicates are re-ACKed and not re-processed; ACKs are never ACKed.
- **Keep-alive:** `PING` every `KeepAliveInterval` (15 s); the peer's `PONG` echoes our timestamp → RTT (`RoundTripTime`, `RoundTripTimeUpdated`). **Watchdog:** any valid inbound packet resets a counter; `MaxMissedKeepAlives` (3) silent intervals → link lost. `POKE`/`PING` from the peer are answered with `PONG`; `LAGRQ` with `LAGRP` (timestamp echoed).
- **Validation:** only packets from the dialled endpoint are accepted; frames must carry our call number as destination and the peer's call number as source; mini frames must match the established peer.
- **Audio frames:** the first voice frame of each transmission (and every 16-bit timestamp wrap) is sent as a *full* Voice frame (subclass = codec 0x04 µ-law); subsequent frames are 4-byte-header **mini frames** (low 16 bits of the ms timestamp).
- **Disconnect reason:** `DisconnectReason` ∈ {None, LocalRequest, RemoteHangup, RemoteRejected, RemoteBusy, LinkLost}, set *before* `StateChanged(Disconnected)`. Only `LinkLost` is auto-recoverable.
- **Threading rule:** events (`StateChanged`, `RemoteKeyedChanged`, `VoiceFrameReceived`, …) fire on the **receive/keep-alive/retransmit threads**. Never dispose the session from inside a handler (it used to deadlock; `DisposeAsync` now bounds its waits at 2 s, but handlers should still hand off to another task, as `NodeRuntime` does).
- **Limitations:** IPv4 only; binds an ephemeral UDP port (the `IaxPort` setting is only *advertised* to the registry); G.711 µ-law only; no call transfer; `AST_FRAME_TEXT` is surfaced raw (see §14 on app_rpt text protocol).

### 4.3 `NodePttController`
Sources: UI button, Bluetooth, media key, VOX. Modes: **Momentary** (hold) / **Toggle** (tap to start, tap again to stop; UI release ignored; media key always toggles). A **Time-Out Timer** (default 180 s, per-key-up restart) force-unkeys with source `External`. Sends `AST_CONTROL_KEY` (0x0C) / `UNKEY` (0x0D) through the session; `SetPttStateAsync` is idempotent and thread-safe. Disposal unkeys. **`HandleButtonAsync(pressed, source)` (2026-11-06)** is the one place a physical button follows the PTT mode (Momentary: hold to key; Toggle: each press flips, release ignored); Bluetooth and the mapped phone button both use it. **`Policy` (`TransmitPolicy`, 2026-11-06):** keying is refused (with a `WarningLogged` explaining why) while transmit is blocked, and an active key is released if it becomes blocked; unkeying is never blocked. `TransmitPolicy` (owned by `NodeRuntime.Transmit`) blocks when `ListenOnly` (from settings) or `MicrophoneAllowed` is false (set by a platform head; Android does this from `RECORD_AUDIO`). **Listening never depends on it**: with the mic denied or listen-only the app still connects and plays received audio (the mic is opened only while keyed, so it is never opened). **`TransmitEnabled` / `ListenOnlyReason` (2026-11-06):** a per-link gate, default true. With several nodes linked `NodeRuntime` sets it true only on the transmit node's link; keying a listen-only link is refused with a `WarningLogged` ("Node N is listen-only. Choose it as the transmit node…"), and clearing it on a keyed link unkeys it. It is independent of `Policy`.

### 4.4 Audio pipeline (Core/Audio)
- **Format:** 8000 Hz, 16-bit, mono, 20 ms frames (160 samples) everywhere in Core (`AudioFormat`).
- **`IAudioDeviceService`** (implemented per platform): `StartCapture(Action<ReadOnlyMemory<short>>)`, `StopCapture`, `StartPlayback`, `StopPlayback`, `EnqueuePlaybackPcm`, `BufferedPlayback`, `ErrorOccurred`. Capture callbacks arrive on the driver's audio thread with arbitrary block sizes.
- **`NodeAudioEngine`:** capture → `PcmFramer` (exact 160-sample frames) → mic gain → level meter → (only while PTT keyed) µ-law → bounded drop-oldest queue (8) → `Session.SendAudioFrameAsync`. Receive: `VoiceFrameReceived` → µ-law decode → `JitterBuffer` → 20 ms playout clock (5 ms timer, catch-up, stall protection) → device. Skips frames if the device backlog exceeds 200 ms. `KeepAudioPipelineAlive` writes zero PCM during silence (**iOS keeps its audio session/background execution alive this way**).
- **`JitterBuffer`:** timestamp-keyed (16-bit wrap safe), prime depth 3 frames (60 ms), capacity 25, reorder, drop late/duplicate, conceal up to 3 lost frames by repeating the last frame with halving gain, re-prime after idle; thread-safe.
- **`PcmResampler`:** streaming area-averaging resampler to 8 kHz (used on iOS capture; hardware is usually 44.1/48 kHz). **`AudioLevelMeter`:** RMS/peak with attack/release ballistics; UI maps RMS −60…0 dBFS to 0…1.
- **Transmit voice clean-up (`MicProcessor`, added 2026-11-05 after the first on-air test sounded "mostly clear"):** G.711 does nothing to the signal (8 kHz sampling + log quantisation), and the first live test sent the raw mic at only −28 dBFS peak. Per 20 ms frame, after `MicGain` and before the VU meter/encoder (`NodeAudioEngineOptions.MicProcessing`, driven by the `MicProcessing` setting, default **on**, checkbox in Settings): (1) 2nd-order Butterworth **high-pass at 300 Hz**; (2) slow **AGC** towards −21 dBFS RMS (≈ −6 dBFS speech peaks): gain range −12…+24 dB, attack 0.5/frame, release 0.03/frame (~0.7 s), **gate at −50 dBFS** — pauses/noise never raise the gain and it drifts back to 0 dB (0.02/frame) so hiss is not left boosted; gain is ramped across each frame (no clicks); (3) **soft limiter** (tanh knee from 70% of a 0.89 FS ceiling) instead of hard clipping. Unit-tested (rumble −18 dB+, 1 kHz within 1.5 dB, quiet voice lifted to target, loud voice tamed under the ceiling, pauses not amplified, silence stays silence, ramped gain). **Not yet confirmed by ear on air** (A/B with the owner pending).
- **Voice timestamps (`Iax2Session.NextVoiceTimestamp`):** mini-frame timestamps used to be the wall clock at send time, which measured live as 16–24 ms spacing around a nominal 20. Now each voice frame is stamped previous + its own duration (20 ms), nudged ≤ 1 ms/frame towards the wall clock when it is > 5 ms away (so drift never accumulates), and restarted from the wall clock after a gap > 100 ms or at the start of an over. Unit-tested (jitter → 19–21 ms spacing, 1 % clock drift stays within 8 ms over 60 s, gap resync, 16-bit wrap).
- **Several links = several engines (2026-11-06).** Each `NodeLink` keeps its own `NodeAudioEngine` **and its own `IAudioDeviceService`** (the runtime calls the audio factory once per link); the OS mixes the outputs (WaveOut / AudioTrack / AVAudioEngine each play independently), so nothing in the verified single-link audio path changed. Only the transmit link ever opens the mic (it opens only while keyed), so there is still one capture. `NodeAudioEngine.Muted` silences one link: audio is still decoded, metered and reported via `RemoteAudioActiveChanged` (the row still shows "Receiving"), but nothing is enqueued to the device (zeros instead when `KeepAudioPipelineAlive`, so iOS keeps its session). **No cross-link repeating:** audio from one node is never forwarded to another (NodeX is an endpoint, not a hub). **Unverified:** that Android and iOS tolerate two or more simultaneous `AudioTrack` / `AVAudioEngine` instances (expected, never run); echo is worse with several streams on open speakers.
- **Known behaviour:** all heads (desktop, Android, iOS) set `OpenMicOnlyWhileKeyed`, so the mic is open only while keyed (+3 s hold-off) — see the Appendix. The input meter is idle while the mic is closed. Desktop enabled 2026-11-06; not yet heard/checked live (first words after keying may clip).
- **Echo (desktop):** there is no acoustic echo cancellation on desktop; with open speakers and a live mic the received audio will be re-transmitted. Use a headset. Mobile uses the platform's voice-communication processing (§7). Not yet addressed.

### 4.5 `NodeLink` and `NodeRuntime` (Core/Link)
- **`NodeLink`** = one call: `Iax2Session` + `NodePttController` + `NodeAudioEngine` + its device. Logs in as IAX user **`radio`** with `CALLING_NUMBER` = local node (see §5.4). Audio starts when the session reaches `Connected` (off the receive thread). `WasConnected` records whether it ever connected.
- **`NodeRuntime`** is the UI-toolkit-independent coordinator. API: `StartAsync`, `ApplyConfigAsync`, `ConnectAsync(targetNode)`, `DisconnectAsync`, `Link`, `Config`, registration state (`IsRegistered`, `RegistrationStatus`, `PerceivedAddress`), and events `RegistrationChanged`, `LinkChanged`, `LinkActiveChanged(bool,node)`, `ReconnectChanged(ReconnectStatus)`, `ErrorOccurred`, `Notice`. Saving settings (via `NodeConfigService.Changed`) re-applies configuration and (re)starts registration automatically.
- **`LinkActiveChanged`** means "a node link is *wanted*" — it stays `true` across automatic reconnects so platform heads don't tear down background services mid-recovery. Android uses it to start/stop the foreground service. With several nodes the node argument (and `BackgroundState.LinkedNode`) is a comma-joined list, e.g. "27339, 55553", so the notification reads "Linked to node 27339, 55553".
- **Multiple nodes at once (2026-11-06; design: Flowstep "NEW Multi-node links - phone").** `NodeRuntime` keeps one **`NodeConnection`** per wanted node (public read-only view: `Node`, `Info`, `Link`, `Reconnect`, `IsTransmitTarget`, `Muted`); each owns its own reconnect loop, early-hang-up counter and `Wanted` flag — the per-link logic of §4.6 is unchanged, just per node. API: `ConnectAsync(node)` **adds** a link (max `MaxLinks` = 4, else `InvalidOperationException`); connecting to a node that is already linked redials just that node and keeps its transmit/mute choice; `DisconnectAsync()` hangs up all, `DisconnectAsync(node)` one; `Connections`, `Links` (live calls), `TransmitNode`, `SetTransmitNode(node?)`, `SetMuted(node, bool)`; events `LinkAdded`, `LinkRemoved`, `ConnectionsChanged` (any change: set, state, transmit, mute, reconnect). **`Link` still exists and means the transmit node's live call** (null when none/between attempts); `LinkChanged` was removed (replaced by `LinkAdded`/`LinkRemoved`); `Reconnect`/`ReconnectChanged` report the first node that is recovering (per node: `NodeConnection.Reconnect`).
  - **Transmit rules (safety):** exactly one link may key. The first node linked becomes the transmit node; later ones are listen-only until the user picks one (`SetTransmitNode`; an active key on the old node is released). If the user hangs up the transmit node and exactly one link remains, that one becomes the transmit node; **if the far end drops the transmit node, the choice is left empty** (notice "Node N ended. Choose a node to transmit to.") and PTT is inert until the user chooses — a transmission is never silently redirected to a different RF node. A transmit node that is only reconnecting stays the transmit node. DTMF (keypad and macros) goes to the transmit node only.
  - **Network handoff** now re-dials every established link (each node recovers on its own loop); registration is refreshed once.
  - Errors from a link are prefixed "Node N: " when more than one node is linked.

### 4.6 Auto-reconnect and network handoff (spec of record)
**Auto-reconnect** (setting `AutoReconnect`, default on) re-dials when an **established** link is lost:

| Link ended because… | Re-dial? |
|---|---|
| Keep-alive watchdog / retransmit limit / INVAL (`LinkLost`) **and** the link had been `Connected` | **Yes**, with back-off |
| `LinkLost` but the call was **never answered** | No — error "No response from node N" |
| Remote hung up **within `EarlyHangupWindow` (3 s) of ACCEPT** (`RemoteHangup`, `NodeLink.ConnectedDuration` below the window) | **Yes**, back-off `InitialDelay`×`Factor`^(n−1) capped at `MaxDelay`, up to `EarlyHangupMaxRetries` (30 ≈ 28 min; was 10 until 2026-11-06, when 27339 accepted only on retry 10); notice "Node N hung up straight after accepting; it probably doesn't know this node yet (a busy hub can take 10 minutes or more). Retrying in X s (retry n of 10)"; after the limit error "Node N keeps hanging up right after accepting the link…" and stop. Why: live-verified that an ASL3 node accepts, then app_rpt hangs up ~90 ms later ("Reported node N cannot be found") until its own node list has learned this node (minutes after registering or after an address change). Needs `AutoReconnect` on. The counter resets on a new user Connect and when a link stays up past the window. |
| Remote hung up later than that (`RemoteHangup`) | No — notice "Node N disconnected" |
| Remote rejected / busy | No — error |
| User pressed Disconnect | No (also cancels any reconnect cycle) |
| `AutoReconnect` off | No — error "Link to node N lost" |

Back-off (`ReconnectPolicy`): first retry after `InitialDelay` 2 s (immediately after a network change), ×`Factor` 2 per failure, capped at `MaxDelay` 60 s, each attempt must reach `Connected` within `AttemptTimeout` 12 s, `MaxAttempts` null (unlimited) by default. While there is no usable network the loop waits (re-checking every 5 s or on any network event) instead of burning attempts. Status is published as `ReconnectStatus(Active, Attempt, NextAttemptIn, Message)`.

**Network handoff:** `INetworkMonitor` (default `SystemNetworkMonitor`: `NetworkChange` + `NetworkInterface`) reports changes; events are **debounced 1.5 s** and compared by *signature* (set of up, non-loopback, non-tunnel, non-link-local IPv4 addresses) so DHCP renewals are ignored. On a real change: (1) **re-register immediately** (so the registry/remote nodes learn the new address; waits up to 5 s), (2) if a link was established, end it and **re-dial immediately** from the new address (an IAX2 call is bound to the source address and cannot migrate), (3) if the network vanished, wait for it to return. Where the platform restricts network enumeration (recent Android) `SystemNetworkMonitor` degrades to "assume available, signature unknown" and the keep-alive watchdog still catches the loss (~45 s). Android uses `AndroidNetworkMonitor` (ConnectivityManager callback) instead.

### 4.7 Settings, secrets, registration, resolution
- **`NodeSettings`** (JSON, `%LOCALAPPDATA%\NodeX\settings.json`, atomic write, corrupt file kept as `.corrupt`): `NodeNumber`, `Callsign`, `ListenOnly` (default false), `DefaultTargetNode`, `FavoriteNodes`, `RegistrationUrl`, `IaxPort`, `RegisterOnStartup`, `AutoReconnect`, `InputDeviceId`/`OutputDeviceId` (reserved, not yet used by any backend), `MicGain`, `MicProcessing` (default true), `TimeOutTimerSeconds`, `TogglePtt`, `HardwarePttKey` (Android key code of a phone button used as PTT; 0 = none), `IdReminderEnabled` (default **false**), `IdReminderMinutes` (default 10, valid 1–60). **The password is never in this file.**
- **`NodeSettingsValidator`:** node number 3–7 digits; password required, ≤79 chars (ASL buffer is 80); callsign `[A-Za-z0-9]{1,6}(/[A-Za-z0-9]{1,6})?` and **required unless `ListenOnly`** (a callsign that is given is still checked in listen-only mode; the app does not verify it against any licence database); https registration URL; port 1–65535; mic gain 0.1–8; TOT 10–600 s; favourites must be node numbers. Normalisation trims, upper-cases the callsign, de-duplicates favourites.
- **`NodeConfigService`:** validates, then writes the **password to `ISecretStore` first**, then settings; raises `Changed`. Invalid input writes nothing.
- **Secret storage per platform:** Windows **DPAPI** (`CurrentUser`); Linux/macOS AES-GCM with a random key in a 0600 file (weaker than a keyring — documented); Android **Keystore** AES-256-GCM key + ciphertext in private SharedPreferences; iOS **Keychain** with `AfterFirstUnlockThisDeviceOnly` (readable in the background with the screen locked, not migrated to backups/other devices). An unreadable secret is treated as "not stored" (user re-enters it).
- **`AslRegistrationService`:** see §5.1. Exposes `PerceivedAddress`, `PerceivedPort`, `ServerRefresh`; interval = configured (60 s) but never longer than the server's `refresh`.
- **`NodeResolution`:** static overrides → 15-minute cache → ASL stats REST lookup → DNS A record (`<n>.nodes.allstarlink.org`, then legacy `node<n>.allstarlink.org`; IPv4 preferred). Injectable HTTP/DNS/clock for tests.
- **`NodeDirectory` (Core/Services, 2026-11-06) — "Find a node" data source.** Uses the stats website's own list feed: `POST https://stats.allstarlink.org/api/stats/nodeList` (server-side DataTables; found in the page's `nodeList.js`, response shape verified live 2026-11-06). Form fields: `start`, `length`, `search[value]` (global, contains), `columns[i][search][value]` (per column, contains), `order[0][column|dir]`. Columns: 0 node (HTML link), 1 bubble, 2 callsign/owner, 3 "Freq" (free text: a frequency or a name), 4 tone, 5 location, 6 country, 7 site, 8 affiliation, 9 connections ("" = none). **Only columns 0, 2, 3, 7, 8 are sortable on the server** (the page marks 1, 4, 5, 6, 9 non-orderable), so "most connected" and sort-by-country are not offered. **It is not a documented public API:** it can change, and it returns HTTP 429 when hit too fast (seen once while probing). So: one request at a time, 2-minute cache of identical queries, 450 ms typing debounce in the UI, 50 rows per page, a `NodeX/1.0` User-Agent, failures reported as `NodeDirectoryException` with a screen-ready message. **Open item: ask AllStarLink (the page credits KK9ROB / WD6AWP) whether a supported search API exists or whether this use is acceptable.** Digits-only free text is routed to the node column. Test: `NodeDirectoryTests`, `NodeSearchViewModelTests`.

### 4.8 UI layer
**Find a node (2026-11-06, desktop-verified once, not on a phone):** `NodeSearchViewModel`/`NodeSearchView` full-screen overlay opened from "Find a node…" on the Connect card (designed in Flowstep first: file "NodeX UI Review", screen "NEW Find a node - phone"). One search box (type-as-you-go), one-tap country chips, "More filters" (country, frequency or name, callsign), sort (node number / callsign / frequency-name), result cards (node, callsign, description + tone, place, linked count, ★ favourite, **Use** which only fills the connect box — it never connects), "Show more" paging, friendly error with Try again. ★ saves via `NodeConfigService` (needs complete settings); `NodeRuntime` no longer re-registers when only non-registration settings change. **Phone button as PTT (Android, 2026-11-06):** `Core/HardwarePttMapper` maps one key code; Settings → "Phone button for push-to-talk" → **Choose button** captures the next key (Back/Home/Power/Recents/Menu are never captured), **Clear** removes it. `MainActivity.DispatchKeyEvent` feeds every key to it; the mapped key is consumed (so a volume key no longer changes volume) and drives `NodePttController.HandleButtonAsync`, so it honours hold vs Toggle PTT (Settings checkbox "Toggle PTT" and the Mode pill). **Limitation: works only while NodeX is on screen** (activity key events); screen-off / background headset buttons would need a `MediaSession` in the foreground service (not built). Tests: `HardwarePttMapperTests`, `PttButtonTests`. **State/city filters:** the list has one free-text Location column (no state, county or city fields; ~half of US rows are "City, ST", others "Houston/Texas", "Las Vegas NV", a bare city, or junk). City → Location column filter. State → two server queries (the code and the full name, restricted to Country = United States, up to 2 x 200 rows each) merged, de-duplicated and re-checked by `UsStates.Matches` (token-aware: "West Virginia" is not Virginia, "Kansas City" is not Kansas, lower-case "or" is not Oregon), sorted/paged locally; a banner says it is best effort and when the result was truncated (live check 2026-11-06: TX 660 + Texas 359 raw matches). **County is not in the data and cannot be filtered.** Tests: `UsStateSearchTests`, `NodeSearchViewModelTests`. **Transmit-blocked UI:** PTT button disabled and relabelled "LISTEN ONLY"/"MIC OFF", amber banner with the reason and an **Allow microphone** (Android: system prompt, or app settings after "Don't ask again") or **Add callsign** button. iOS does not yet feed `MicrophoneAllowed` (unverified there).

- **Palette** (App.axaml, dark): `ConsoleBackgroundBrush #0D1117`, `CardBackgroundBrush #161B22`, `CardBorderBrush #30363D`, `HeaderBackgroundBrush #1F242C`, `TxRedBrush #EF4444`, `RxGreenBrush #10B981`, `AmberAccentBrush #F59E0B`, `CyanAccentBrush #06B6D4`, text `#F0F6FC` / `#8B949E` / muted `#848D97` (was `#484F58`, only 2.1:1 on cards; now 5.1:1 on cards, 4.6:1 on the header = WCAG AA), `DangerButtonBrush #DC2626` (fill for buttons carrying white text such as Disconnect: 4.8:1; `#EF4444` gives only 3.8:1). Reuse these via `StaticResource`; do not hard-code new colours.
- **`MainView`:** header (branding, station callsign/node, ASL-registered badge, BLE badge [static], **Settings** button), transceiver card (state badge, latency, node display, RX/TX indicators, real VU meters, TOT bar), PTT button (press/hold handled in code-behind with tunnelled pointer events; toggle via click), connect card (node box, Connect/Disconnect, favourites), DTMF keypad + three function macros, event log. The **settings overlay** is a full-screen `Border` bound to `IsSettingsOpen` containing `SettingsView` — chosen over a separate window so it works on phones.
- **Responsive layout (`MainView.axaml.cs`):** one set of named cards (`HeroCard`, `ConnectCard`, `RxTxRow`, `MetersCard`, `PttCard`, `DtmfCard`, `LogCard`) is re-parented by width — **no separate mobile view, so bindings are shared**. Width < `NarrowBreakpoint` (720): single scrolling column ordered for operating (hero → connect → RX/TX → meters → keypad → log) and the **PTT card is moved into `PttDock`** (bottom row of the root grid, always visible, full-width 96 px button, thumb reach). Width ≥ 720: left column hero/RX-TX/meters/PTT, right column (360 px) connect/DTMF/log. **Height rule (`ShortHeightBreakpoint` 520):** a window shorter than 520 px also docks the PTT card (so a landscape phone, ~860×420, keeps PTT reachable while the two columns remain); in that case the PTT button is 64 px high, the PTT info text is hidden and the card padding shrinks. The header has its **own breakpoints** (`HeaderMode` enum): `Wide` ≥ 920; `Compact` < 920 (rows: brand + Settings / station / badges); `Short` when also height < 520 (brand | station | Settings on one row, badges on a second, tagline hidden, to save vertical space). The root gets the `narrow` class (width < 720 only), which enforces ≥ 44 px button/text-box heights and larger keypad keys. `ApplyLayout(narrow, isShort)` derives `dock = narrow || isShort`. `MainWindow.MinWidth` is 340 so the phone layout can be exercised on desktop. **Gotcha:** when switching to fewer grid columns every child's `Grid.Column` must be ≤ the new column count (a stale index in a hidden child crashed Avalonia's grid measure).
- **PTT button face:** the `Button` is transparent and the visible face is the `PttFace` Border inside it (classes `keyed` toggled by `IsTransmitting`); styling the Fluent template presenter did not take effect, and this also prevents hover/press from overriding the red keyed state. The keyed (red) state has **not been seen on screen yet** (needs a live link).
- **State is never colour-only:** the RX and TX cards show text ("Idle"/"Receiving", "Idle"/"ON AIR") in addition to colour, with `AutomationProperties.LiveSetting` (Polite for RX, Assertive for TX). The BLE dot is grey when not connected and cyan when `IsBleConnected`. The PTT-mode button is a visible bordered pill with a "⇄ tap to switch" hint. Interactive controls, meters and the settings text boxes carry `AutomationProperties.Name` for screen readers (**not yet tested with a real screen reader**). The tagline reads "AllStarLink Radioless Node".
- **Settings screen layout:** `SettingsView` root is `Grid RowDefinitions="*,Auto"`: the scrolling form is row 0; row 1 is a pinned action bar (error/status banners + Test registration / Cancel / Save) so the actions are always visible, including on short phone screens.
- **`MainViewModel`:** thin adapter. All state changes from Core threads are marshalled with `OnUi(...)` (`Dispatcher.UIThread`); audio levels are stored in volatile fields and applied by a 100 ms `DispatcherTimer`. Opens Settings automatically on first run. Reconnect status drives the badge ("RECONNECTING (n)") and keeps Disconnect enabled so the user can cancel.
- **DTMF macros:** `*3<node>` (link), `*1<node>` (unlink) using the Connect box's node, `*70` (status); digits sent one per 120 ms via `Session.SendDtmfAsync` — **to the transmit node's link only** (§4.5).
- **Linked nodes card (2026-11-06, `LinksCard`, `LinkItemViewModel`):** shown once anything is linked; header "Linked nodes" + "n / 4 linked"; one row per node: "<node> · <favourite name>" (else "Node N"), callsign • description, text status (CONNECTED / CONNECTING… / RECONNECTING (n)) + "RTT n ms", a green **Receiving** pill when audio is arriving from that node, a red **TRANSMIT HERE** tag with a red left edge on the transmit node or a **Listen only** tag + **Transmit here** button on the others, **Mute / Muted · tap to hear**, and **Disconnect** (per node). **Connect** in the Connect card now adds a link (hint text; button stays enabled until 4 are linked); its Disconnect button becomes **Disconnect all** when more than one is linked. The hero card shows the transmit node (and "also listening: …"), or "No transmit node" with a hint. The PTT key is inert (headline **CHOOSE TX NODE**, caption NO TX NODE, amber banner "Pick which linked node to transmit to.") when nodes are linked but none is chosen (`NeedsTransmitNode`/`CanKey`); with several nodes the caption reads "TO NODE n". RX card = any link receiving; speaker meter = loudest link; mic meter = transmit link. Log lines are tagged `[node]` when more than one is linked. Placement: narrow = after the hero card; wide = top of the right column. **Not seen on screen with real links.**
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

---

## 6. Runtime behaviour reference (numbers in one place)

| Parameter | Value | Where |
|---|---|---|
| Keep-alive interval / misses before dead | 15 s / 3 | `Iax2Session.KeepAliveInterval`, `MaxMissedKeepAlives` |
| Retransmit timer / max | 1 s initial → 2×RTT (100 ms–10 s), ×2 per retry, cap 10 s / 4 retries | `Iax2Session`, `Iax2RetransmitQueue` |
| Registration interval | 60 s (≤ server `refresh`) | `AslRegistrationConfig` |
| Reconnect delay | 2 s ×2 … cap 60 s; attempt timeout 12 s; unlimited | `ReconnectPolicy` |
| Network-change debounce / registration wait | 1.5 s / 5 s | `ReconnectPolicy` |
| Jitter buffer | prime 3 frames (60 ms), capacity 25, conceal ≤3 | `JitterBuffer` |
| Device backlog cap | 200 ms | `NodeAudioEngineOptions` |
| TOT default | 180 s | `NodeSettings`, `NodePttController` |
| Resolution cache | 15 min | `NodeResolution` |
| Password max length | 79 | `NodeSettingsValidator` |

---

## 7. Mobile background execution

### Android (`NodeX.UI.Android`) — implemented, **not device-tested**
- `NodeForegroundService` (types `microphone|mediaPlayback|connectedDevice`; typed `StartForeground` overload used on API ≥ 30), started/stopped from `NodeRuntime.LinkActiveChanged` (only if `RECORD_AUDIO` is granted; Android 14 refuses a microphone service otherwise). Holds a **partial wake lock** and a **Wi-Fi lock** (`FullLowLatency` on API ≥ 29, `FullHighPerf` below) via `AndroidBackgroundKeeper`.
- **Bluetooth SCO** routing (`SetCommunicationDevice` on API ≥ 31, legacy `StartBluetoothSco` below) — switches audio mode to *in-communication* **only when a Bluetooth headset is actually present**.
- `MainActivity` requests `RECORD_AUDIO`, `POST_NOTIFICATIONS` (API ≥ 33), `BLUETOOTH_CONNECT` (API ≥ 31) at startup. Manifest declares all required permissions and foreground-service permissions. `MainActivity.OnResume` asks **once** (pref `battery_exemption_asked`) for the battery-optimisation exemption, only after a link is up (so it appears on the next resume after the first connect). **`AndroidNetworkMonitor`** (`ConnectivityManager.RegisterDefaultNetworkCallback`, event-driven, signature = network handle + IPv4 addresses) replaces `SystemNetworkMonitor` on Android. **Battery model:** mic closed unless keyed; UI meter timer stopped while backgrounded.
- Audio: `AudioRecord`/`AudioTrack`, 8 kHz mono, `VoiceCommunication` source/usage (platform echo cancellation); non-blocking writes; `BufferedPlayback` from playback head position.
- Use `OperatingSystem.IsAndroidVersionAtLeast(n)` guards (the CA1416 analyzer understands them; `Build.VERSION.SdkInt` comparisons produce warnings).

### iOS (`NodeX.UI.iOS`) — implemented, **not device-tested**
- `Info.plist`: `UIBackgroundModes` = `audio`, `bluetooth-central`; microphone and Bluetooth usage strings.
- `IosAudioSessionManager`: `PlayAndRecord`, options `AllowBluetooth` (**this is the HFP option; the .NET binding has no `AllowBluetoothHfp` name**) | `MixWithOthers` | `DefaultToSpeaker`, mode `VoiceChat`, preferred 8 kHz / 20 ms buffer; observes interruptions and route changes. One shared instance (it registers observers).
- `IosAudioDeviceService`: `AVAudioEngine`; input tap at the hardware rate → `PcmResampler` → 8 kHz PCM16; playback via `AVAudioPlayerNode` (8 kHz float buffers; the engine resamples). Rebuilds the engine after an interruption ends. Use `IosAudioDeviceService.RecommendedEngineOptions` (zero-PCM keeper) when registering services.

---

## 8. Bluetooth hardware & PTT model — **not implemented**
Contract and parser exist; no platform implements `IBluetoothPttService`.
- **Audio:** OS Hands-Free Profile (HFP/SCO) appears as an ordinary mic/speaker route (Android SCO handled above; iOS via `AllowBluetooth`).
- **PTT triggers (planned):** BLE GATT notifications — press `0x01`/`+PTT=P`, release `0x00`/`+PTT=R` (`BluetoothPttPacketParser` also supports learned patterns); **Learn mode** UI wizard (connect → subscribe to all notifying characteristics → "press and HOLD" → record press payload → "RELEASE" → record release payload → persist per device); **media-key fallback** (Play/Pause → latch toggle).
- The header's "BLE PTT" badge is currently a static "not set up".

---

## 9. UI specification & known gaps
- **Phone layout:** implemented (§4.8). **Still to verify/do on real devices:** iOS safe-area insets (notch/home indicator — Avalonia lays out inside the safe area by default, unconfirmed), Android system-bar/gesture insets, soft-keyboard behaviour when typing a node number (the PTT dock should not jump or cover the field), landscape phones (the height rule docks PTT and uses a short header; verified only by resizing the Windows app to 860×420), tablets, display scaling, and haptic feedback on PTT press (not implemented).
- **UI review backlog (Flowstep-assisted review, 2026-11-05; not done):** portrait-phone header still takes 3 rows (slim it); event log can trap scrolling inside the page scroll on touch; larger-text option and safe-area handling (needs a device); window icon is still the Avalonia logo and the window title still says "(Asterisk-Free)"; no spacebar PTT or haptics; no light/high-contrast theme; Disconnect has no confirmation; the TX-active (red) state has not been captured on screen. The Flowstep file ("NodeX UI review") and its published review page show the *pre-fix* UI.
- No audio-device pickers yet (settings fields `InputDeviceId`/`OutputDeviceId` reserved).
- No runtime permission rationale UI.
- Event log is text-only; remote status text frames (`AST_FRAME_TEXT`) are shown raw.
- Light theme untested (palette is dark-only).

---

## 10. Security & privacy requirements
- The node password lives **only** in `ISecretStore` and in memory; never in settings JSON, logs, status messages, UI error text, or any IAX packet (tests assert each of these).
- Registration uses HTTPS only (validator rejects non-https URLs).
- Only the dialled endpoint may send to the call socket; spoofed/other-call packets are dropped (tested).
- No telemetry. The only outbound traffic: registry (HTTPS), directory/DNS lookups, and IAX2 to the linked node.

---

## 11. Testing

`tests/NodeX.Core.Tests` (xUnit, **343 tests**, ~7 s). It references Core **and** `NodeX.UI` (view-model tests). `MainViewModel` itself is untested (needs an Avalonia dispatcher).

| File | Covers |
|---|---|
| `G711UlawTests`, `PcmResamplerTests`, `AudioTests` | codec, resampler, framer, level meter, jitter buffer, audio engine (fake device/channel) |
| `Iax2ProtocolTests`, `Iax2RfcConformanceTests` | wire format; **every IAX/control subclass value vs RFC 5456** |
| `Iax2ReliabilityUnitTests`, `Iax2ReliabilityTests` | mod-256 rules, retransmit queue, VNAK, duplicates, wrap past 255, call-token handshake, sender/call-number validation |
| `Iax2KeepAliveTests` | PING/PONG, RTT, watchdog, silent-peer detection |
| `NodePttControllerTests`, `NodeResolutionTests`, `AslRegistrationServiceTests` | PTT states/TOT, resolution/cache/DNS, registration wire format and parsing |
| `SettingsTests`, `SettingsViewModelTests` | validation, JSON store, secrets never in file, config service, settings screen logic |
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

## 13. Backlog (prioritised) with acceptance criteria

1. **Live-node validation — mostly done** (see `docs/LIVE_VALIDATION.md`). Remaining: re-dial from a genuinely different public IP (e.g. phone on cellular, then Wi-Fi) and whether the early-hangup retry budget is long enough; RX audio quality with real speech from a remote; A/B the `MicProcessor` voice clean-up on air; optional DTMF_BEGIN/END polish; parse link TEXT frames (`L …`, `T … STATUS`) for a "linked nodes" panel.
2. ~~Phone-friendly layout~~ — **done** (§4.8, verified by resizing the desktop app). Remaining: confirm on real phones/tablets and fix landscape-phone density, insets and keyboard behaviour (§9).
3. **Bluetooth PTT**: implement `IBluetoothPttService` for Android (BLE GATT), iOS (CoreBluetooth), desktop (Windows BLE/`InTheHand`/WinRT), plus the Learn wizard UI and media-key fallback; wire status into the BLE badge.
4. **Cross-platform desktop audio** (Linux/macOS): add a backend (spec originally named SoundFlow/MiniAudio; OpenAL/PortAudio are alternatives) behind `IAudioDeviceService`; audio-device pickers for all platforms using `InputDeviceId`/`OutputDeviceId`.
5. ~~Open the mic only while keyed~~ — **done on all heads** (`OpenMicOnlyWhileKeyed`, Appendix; mobile device-untested, desktop not yet checked live).
6. ~~Android connectivity monitor~~ — **done** (`AndroidNetworkMonitor`, battery-optimisation prompt; device-untested). Remaining: permission-denied UX.
7. **Device testing** on a physical Android phone and iPhone (background audio for ≥1 h screen-off, Wi-Fi↔cellular handoff, Bluetooth headset).
8. **Housekeeping:** ~~`git init`~~ (done, private GitHub repo); `global.json`; delete `NodeX.UI.Browser`; CI (Windows build + test; macOS runner for iOS signing); `.editorconfig`; README and LICENSE.
9. **Nice to have:** parse ASL status/linked-node text frames into a "linked nodes" panel; bind configured `IaxPort` to the socket; IPv6; additional codecs; inbound mode over VPN.
10. **Optional station-ID reminder** (§17.1): ~~settings, Core timer, UI prompt, tests~~ done; **remaining: Android/iOS background notification** (subscribe to `MainViewModel.IdReminderDue`) and a visual check of the banner. Remaining compliance decisions: compliance decisions (§17.2–3): licensing wording and whether `Callsign` is required before keying.
11. **Desktop echo handling** (§4.4): headset advisory in the UI or software echo cancellation.
12. **Linux/macOS secret storage:** use the OS keyring (libsecret / macOS Keychain) instead of a key file beside the ciphertext (§4.7).

---

## 14. Unverified assumptions & risks (check before relying)

**Verified live against node 523800 on 2026-11-05** (details in `docs/LIVE_VALIDATION.md`): ASL registration; call-token handshake (empty CALLTOKEN IE on the first `NEW`); link login as `radio` with no password and `CALLING_NUMBER` = our node; keep-alive both ways; keying (`KEY`/`UNKEY` + voice) relayed on air; DTMF function codes (`*70`); real Windows mic → G.711 → network; Windows speaker output; re-dial from the same IP while a stale call exists. **Still unverified:** everything in the list below, plus voice-cleanup quality (§4.4) and TX audio quality heard on air.

- **`NodeResolution` REST schema — VERIFIED & FIXED 2026-11-05 against the live service.** The inherited guess (`/api/node/<n>` → `data.ip`) was wrong (404). Real endpoint: `GET https://stats.allstarlink.org/api/stats/<n>` (docs: allstarlink.github.io/developers/api; 30 req/min/IP, cache results). The node's own record is the **top-level `node` object** (`ipaddr`, `port`, `callsign`, `node_frequency`, `Status`, ...); `stats.data.linkedNodes[]` describes its links and must not be used. Checked with node 2530 → 144.202.112.210:4569, identical to DNS `2530.nodes.allstarlink.org` A record (also verified live). Legacy `node<n>.allstarlink.org` returned no data (kept as a harmless last fallback). Nodes that don't report stats return 404 and fall through to DNS.

### Live validation

The dated results of testing against real nodes and hardware (registration, link, keying, DTMF, Windows audio, re-dial) are in [`docs/LIVE_VALIDATION.md`](docs/LIVE_VALIDATION.md). The console harness is `tools/NodeX.LiveCheck` (see §3.1 and that file). Summary of what is **verified** vs **not** is in §2 and the list below.

**Still unverified / open risks:**
- **app_rpt link text protocol:** observed messages are `!NEWKEY1!`, `L <links>` (~30 s) and `T <node> STATUS,…` (reply to `*70`). NodeX never answers `!NEWKEY1!`; 523800 falls back to allowing keying after its `newkeytimer` (~0.9 s) and honoured our early KEY. Other app_rpt versions/configs could behave differently — check app_rpt source (`rpt_link`/`rpt_manager`). Text is shown raw.
- **Reconnect from a genuinely different public IP** (network handoff): same-IP re-dial with a stale call is verified; a new address may be rejected until the remote's node list learns it (see root cause in the live log). The early-hangup retry budget (§4.6, ≈ 6 min) is close to the observed "up to ~5 min, sometimes more" refresh — consider a longer budget.
- **Mic-open-while-connected on mobile** (gated by `OpenMicOnlyWhileKeyed`, Appendix) and iOS background longevity with the zero-PCM keeper (Apple may still suspend the app; needs device testing).
- **Android `NetworkChange`/`AndroidNetworkMonitor`** behaviour on real devices (API 30+).
- **Transmit voice clean-up** (`MicProcessor`, §4.4) A/B by ear on air — pending owner.
- **DTMF end-only frames:** works for function codes; Asterisk logs "emulating" because duration is 0. Optional polish: send DTMF_BEGIN + END ≥ 80 ms.
- The optional ID reminder (§17.1) is implemented and unit-tested but its banner has not been seen on screen and there is no mobile background notification; §17.2–3 are open decisions.

---

## 15. References
- IAX2: RFC 5456 — https://www.rfc-editor.org/rfc/rfc5456.txt (§6.7 keep-alive/lag, §7 reliability, §8.3/8.4 subclass tables).
- ASL3 HTTPS registration source — https://raw.githubusercontent.com/AllStarLink/app_rpt/master/res/res_rpt_http_registrations.c
- ASL `iax.conf` documentation (`[radio]`, `[allstar-public]`, `[allstar-sys]`) — https://allstarlink.github.io/config/iax_conf
- ASL HTTP registration overview — https://allstarlink.github.io/adv-topics/httpreg
- ASL3 project — https://github.com/AllStarLink/ASL3

---

## 16. Change log (high level)
- **Initial scaffold** (Avalonia template + Core stubs) and a first UI by another AI agent (placeholder data, wrong link login, wrong DTMF macros, simulated meters).
- **Protocol hardening:** RFC-correct enums and keep-alive; reliable delivery (sequencing, retransmit, VNAK); LAG replies; sender/call validation; call-token handling; registration rewritten to the real ASL3 format.
- **Audio:** jitter buffer, resampler, engine; Windows (NAudio), Android (AudioRecord/Track + foreground service/locks/SCO), iOS (AVAudioEngine + session) backends.
- **Product layer:** settings + secure password storage (all platforms), `NodeRuntime`/`NodeLink`, real settings screen and main-screen wiring, DTMF macros fixed.
- **Resilience:** disconnect reasons, auto-reconnect with back-off, network-change handoff with immediate re-registration.
- **Phone layout:** responsive main screen (single column + docked PTT bar < 720 px, compact header < 920 px, 44 px touch targets); DTMF pad as a data-driven list; time-out counter now shows the configured limit (was hard-coded "03:00"); PTT button face redrawn so state colours can't be overridden by the theme; "Quick Recall" hidden when there are no favourites.
- **UI review fixes (2026-11-05):** muted-text and Disconnect contrast raised to WCAG AA; height-aware PTT docking + Short header for landscape phones; text RX/TX state with live-region settings; accessibility names; visible PTT-mode toggle; BLE dot state; settings actions pinned; tagline "AllStarLink Radioless Node". Verified by screenshots of the Windows build at 1050×760, 800×760, 420×860, 860×420.
- **Owner feedback batch (2026-11-06):** favorites have friendly names (`NodeSettings.FavoriteLabels`, node→name; edited from a name box + ★ under the connect box, which also favorites any typed/pasted node); tapping a favorite or "Use" in Find-a-node now connects immediately; Event Log has a Copy button (whole log, oldest first); Android speaker/phone toggle (`AppServices.SpeakerphoneSetter` → `AndroidAudioRoute`, reset when nothing is linked); Android **Auto Backup** enabled (`allowBackup`, `backup_rules.xml`, `data_extraction_rules.xml`) carrying only `settings.json` — the node password is Keystore-bound and is re-entered after a restore. Disconnect notices now state how long the link was up (diagnosing drops from reflector 27339; a remote hang-up still does not auto-reconnect). **Not built, by decision pending:** accepting inbound calls. (Multiple simultaneous nodes were built later the same day: next entry.)
- **"Registered for ..." badge (2026-11-06):** the header badge reads "ASL Registered · 2 h 05 min". `NodeRuntime.RegisteredSince` (UTC) is set on the false→true registration transition, so the ~60 s refreshes don't reset it; it clears when registration lapses and restarts when a registration setting changes or the app restarts. `DurationText.Format` ("< 1 min", "5 min", "2 h 05 min", "1 d 4 h"); the view model refreshes it once a second while the screen is showing. Tests: `DurationTextTests`, `RegisteredSince_...`.
- **Multiple nodes at once (2026-11-06):** up to 4 simultaneous links, one transmit node (others listen-only), per-node mute/disconnect, per-node auto-reconnect, "Linked nodes" card; see §4.4, §4.5, §4.8. `NodeRuntime` rework (`NodeConnection`), `NodePttController.TransmitEnabled`, `NodeAudioEngine.Muted`; `LinkChanged` removed. 441 tests (13 new). **Not tested live or on a phone.**
- **UI tweaks (2026-11-06):** one node box in "Connect to Node" (star left of it saves that number, a pencil appears for favourites and opens a rename flyout; a new favourite is auto-named from the node's description/callsign); every linked-node card has the same star/pencil; the header has a link strip (one chip per link: number, name, TX/listen/muted/connecting/reconnecting) that stays visible while scrolling. `LinkItemViewModel` gained favourite + chip state. Mockup: Flowstep file "NodeX UI Review", screen "PROPOSED Desktop - links in top bar...". Builds; not yet viewed in the running app.
- **Deploying Windows:** `pwsh tools/deploy-windows.ps1` publishes a self-contained win-x64 Release build and copies it to `C:Program FilesNodeX` (`-Destination` to change, `-StopRunning` to close a running app; needs an elevated shell if Program Files is protected).
- **Reorder favourites (2026-11-06):** drag a favourite chip on the Connect card to reorder; the list moves live and the order is saved on drop (`MainViewModel.MoveFavorite` / `SaveFavoriteOrderAsync`, pointer handling in `MainView.axaml.cs`). A plain click still connects. Mouse only so far; touch not verified (a ScrollViewer may take the drag).
- **27339 drops links at 45 s (2026-11-06):** reproduced with LiveCheck (listen-only, trace): the hub accepts, streams text/LAGRQ/PING normally, we answer everything (PONG, LAGRP, ACKs, RTT 24 ms), then it sends HANGUP (cause code 0, no text) at 45.4 s. Node 523800 held 75 s with the same client, so this is hub-side policy. Cause unknown.
- **FIXED 27339 45 s drop and 45192 unreachable (2026-11-06):** (1) Hubs hang up (IAX HANGUP, cause 0, no text) a link that sends no app_rpt link text for ~45 s even though it answers every PING: `Iax2Session` now sends an empty link list `"L "` every `LinkListInterval` (10 s, as real nodes do; hubs send theirs every ~10 s). Proven live: 27339 held 80 s with it vs 45.4 s without; community reports the same "No link messages from [node] in 46 seconds" hangup. (2) The stats API lists some nodes on a port where they do not listen (45192 on 4579; it answers on 4569, the port in its AllStarLink DNS TXT/SRV record): `NodeResolution.PortProbe` sends an IAX POKE to the listed port and falls back to 4569 if it is silent. Verified live: both nodes held 70 s listen-only. Not yet confirmed in the running app or while transmitting.
- **Build:** build output moved out of OneDrive; iOS and Android build on Windows; solution includes all heads and tests.
- **Live validation (2026-11-05):** registration, link, keying, DTMF, mic/speaker and re-dial verified against a real ASL3 node; early-hangup retry added to auto-reconnect; resolution endpoint fixed; transmit voice clean-up and voice-timestamp pacing added; always-connected battery model (Appendix).
- **Stay registered in the background + Exit (2026-11-06):** `NodeRuntime.Background` (`BackgroundState`: Needed / LinkedNode / Registered) is true whenever settings are complete and "register on startup" is on, or a link is active, until `ExitAsync()`. Android runs `NodeForegroundService` for as long as it is true (idle = `specialUse`, linked = `mediaPlayback|connectedDevice` + `microphone` only if the mic is allowed — which also fixes the service never starting for listen-only users); the service follows the runtime itself so the notification, type and locks change without new service starts. Idle holds no wake/Wi-Fi lock. IP changes while idle re-register immediately (existing network monitor), and returning to the foreground after >30 s calls `NotifyResumed()`. Back = `MoveTaskToBack` (or closes a panel); **Menu → Exit NodeX**, the notification's Exit action, and swiping the task from Recents all run `ExitAsync()` then end the process. iOS: no Exit item; **idle background registration is NOT implemented** (see Appendix).
- **Phone-button PTT and US state/city filters (2026-11-06):** see §4.8. 425 tests.
- **Permissions, callsign, node search (2026-11-06):** `TransmitPolicy` (listen-only / mic denied disables PTT, listening still works); callsign required unless Listen only; Android requests/re-checks `RECORD_AUDIO` and offers "Allow microphone"; `NodeDirectory` + "Find a node" screen; open mic only while keyed now on desktop too; registration no longer restarts on unrelated setting changes.
- **Google Play internal testing (2026-11-05):** app ID set to `com.kv4s.nodex`, Release builds emit a signed `.aab`, upload keystore created (gitignored), releases 1.0 (code 1) and 1.1 (code 2) published to internal testing and installed on the owner's phone; see §3.5.
- **Repo & docs (2026-11-05):** private GitHub repo created; live results moved to `docs/LIVE_VALIDATION.md`; this spec reconciled with them (stale "never tested" statements removed); §17 added.

---

## 17. Amateur-radio compliance (decisions; not legal advice)

NodeX links into ASL nodes that key real RF transmitters (the owner's test node 523800 is permanently linked to RF node 47923). Transmitting through such a link is amateur-radio transmission by the control operator of the *remote RF node* and by the person speaking. The spec previously said nothing about this; item 1 is decided; items 2–4 still need an owner decision before NodeX is distributed to anyone else:

1. **Station identification — DECIDED 2026-11-05 (owner); implemented 2026-11-05 except the mobile background notification.** As built: `Core/IdReminder` (injectable `TimeProvider`; clock starts on the first key-up when none is running, becomes *due* after the interval, stays due until acknowledged or the link ends; later key-ups do not restart it; switching the feature off clears it). `MainViewModel` feeds it from `PttStateChanged`, resets it when the link goes away, and shows an amber "Time to identify (say your callsign)" banner with an **I've identified** button in the hero card, plus an `[ID]` activity-log line; it also raises `IdReminderDue` so a platform head can post a notification (not wired yet). Settings screen: "Station ID reminder" card. Original decision text follows. NodeX transmits **nothing but the user's voice**: no automatic/synthesised ID, no beacons, no tones. Instead it offers an **optional ID reminder** that the user can switch on or off in Settings: while linked and after the user has transmitted, a UI prompt (and a mobile notification when backgrounded) reminds them to identify by voice after the configured interval. Planned design: setting `IdReminderEnabled` (bool; default **off** — opt-in, easy to flip) and `IdReminderMinutes` (default 10, validated range e.g. 1–60); the timer runs only while a link is active and restarts when the user identifies (a "I've identified" tap) or a transmission ends after a long gap; it never keys the radio or injects audio. The user remains responsible for meeting the identification rules that apply to them.
2. **Licensing.** Decide whether the app states that a valid amateur licence is required to transmit, and whether `Callsign` is required before keying is enabled. **Callsign DECIDED 2026-11-06 (owner):** a callsign is required unless the user chooses "Listen only"; listen-only turns PTT off but still connects and listens. The settings screen says a licence is needed to transmit and that NodeX does not verify the callsign. Still open: what the app says in store listings / first-run beyond that.
3. **Third-party / link policy.** Some node owners restrict who may link; NodeX should surface "rejected/busy" clearly (it does) and never retry in a way that looks like abuse (early-hangup retries are capped at 10).
4. **Testing etiquette.** Live tests that key a linked RF node require the owner present and aware (see the safety note in `docs/LIVE_VALIDATION.md`).

---

## Appendix: always-connected battery model (2026-11-05, **not device-tested**)
Requirement: once linked, stay linked until the user disconnects, as cheaply as possible on Android and iOS.
- **Mic gating:** `NodeAudioEngineOptions.OpenMicOnlyWhileKeyed` (set by the Android and iOS heads; default off so desktop/tests are unchanged). `IVoiceChannel.TransmittingChanged` (default no-op; raised by `Iax2Session.SetPttAsync`) opens capture on key; capture closes `MicHoldOff` (3 s) after unkey unless re-keyed. The mic input meter is idle while closed. Cost: first ~tens of ms of speech may be clipped while `AudioRecord`/the tap spins up — verify on devices; raise nothing else.
- **UI:** `MainViewModel.SetForeground(bool)` stops/starts the 100 ms meter timer from `IActivatableLifetime` Activated/Deactivated.
- **Android:** `AndroidNetworkMonitor`; one-time battery-exemption prompt.
- **iOS:** the zero-PCM keeper is deliberately **kept**: the remote streams ~47 packets/s even in silence so it rarely writes zeros, but removing it risks background suspension and cannot be judged without a real iPhone. Revisit with a ≥1 h screen-off test.
- **Unavoidable cost:** the remote sends continuous µ-law (≈47 pkt/s) for the life of the link, which keeps the radio active; only a remote-side change could reduce it.
- Test: `OpenMicOnlyWhileKeyed_OpensOnKey_AndClosesAfterHoldOff`.

## Appendix: stay-registered-while-idle (2026-11-06, **not device-tested**)
Requirement (owner): a configured node stays registered with AllStarLink and tracks IP changes while idle, so connecting later is instant; only a real exit stops it.
- **Core (tested):** `BackgroundState`, `NodeRuntime.BackgroundChanged/ExitAsync/NotifyResumed/HasExited`; tests in `NodeRuntimeReconnectTests` (idle stays needed after disconnect, IP change while idle re-registers, resume re-registers, exit stops everything and later changes do nothing, not-configured and registration-off cases).
- **Android (compiled, not run):** service types and Play declaration — `FOREGROUND_SERVICE_SPECIAL_USE` needs a Play Console foreground-service declaration with the subtype text in `NodeForegroundService`. **Doze risk:** with no wake lock and the screen off for hours, the 60 s heartbeat may be deferred unless the app is exempt from battery optimisation (the app already asks once); if a device test shows lapses, add an `AlarmManager.SetExactAndAllowWhileIdle` heartbeat. Android 15 limits some foreground-service types started from the background, so the service is started from the foreground (app launch / user action).
- **iOS (decision needed):** iOS suspends a backgrounded app within seconds unless it uses a background mode. The only mode available without a server is `audio` (already declared), which today is exercised only while linked via the zero-PCM keeper. Keeping a *registered-but-idle* node alive would mean playing silence the whole time: it costs battery, shows the app as playing audio, and risks App Review rejection. The alternative is VoIP push (PushKit), which needs a push server NodeX does not have. Until decided, iOS registers while the app is open and re-registers when it returns to the foreground (`NotifyResumed`), so after a long absence the first connect can still wait for the registry to catch up.
- **Exit semantics:** ASL has no documented "unregister"; after Exit the registry entry simply ages out.
