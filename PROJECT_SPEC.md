# NodeX — Project Specification & Engineering Guide
### Cross-platform, Asterisk-free AllStarLink node (.NET 10 / Avalonia UI)

> **Last updated:** 2026-11-05 · **Build:** `dotnet build NodeX.sln` → 0 errors, 0 warnings · **Tests:** 306 passing (xUnit)
>
> This document is written for **humans and AI coding agents** who pick the project up cold. Read §0 first.

---

## 0. How to use this document (read first)

1. **Authority order when sources disagree:** (1) the code and its tests, (2) this document, (3) anything else. An earlier "master specification" file existed and contained **wrong protocol details** (wrong registration endpoint, wrong IAX subclass numbers, wrong keep-alive); it has been removed from the folder and must not be recreated or trusted. Protocol facts below are verified against RFC 5456 and the ASL3 source — see §5 and §15.
2. **Be honest about verification.** Nothing in this project has been run against a *real* AllStarLink node or on a *physical* phone yet. §2 and §14 list exactly what is and is not verified. Do not describe unverified behaviour as working.
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
| Node resolution | Implemented, **REST schema unverified** (see §14) | Stubbed tests only |
| PTT state machine (momentary/toggle, TOT, sources) | Implemented | Tests |
| Audio pipeline (jitter buffer, resampler, levels, engine) | Implemented | Tests with fake devices |
| Settings + secure password storage | Implemented | Tests; first-run screen visually checked on Windows |
| `NodeRuntime` (registration, link, **auto-reconnect, network handoff**) | Implemented | Tests with fake node + fake network |
| Desktop head (Windows) | Runs; NAudio WASAPI audio; **never used with real mic/speaker** | Launched and screenshot-checked |
| Desktop head (Linux/macOS) | Compiles; **runs silently** (no audio backend) | — |
| Android head | Compiles/builds; **never run on a device** | Build only |
| iOS head | Compiles/builds; **never run on a device** | Build only |
| Bluetooth PTT (BLE GATT, SCO audio, media key, Learn wizard) | **Not implemented** (interface + packet parser only) | Parser tests |
| Responsive/phone layout | Implemented (single-column phone layout with docked PTT bar below 720 px; two-column desktop above). **Verified only by resizing the Windows app (340–1100 px), not on a real phone** | Screenshots at 340 / 410 / 780 / 1100 px |
| Live-node interoperability | **Never tested** | — |

---

## 3. Repository, tooling and build

### 3.1 Layout (authoritative file map)

```
NodeX/
├── Directory.Build.props            # redirects bin/obj OUT of OneDrive (see 3.3) — keep!
├── NodeX.sln                        # Core, UI, Desktop, Android, iOS, Tests
├── PROJECT_SPEC.md                  # this file
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
│   │   └── NodePttController.cs         # PTT state machine
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
- The folder is **not a git repository**. (Initialising git is a pending housekeeping item.)

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
        └─ creates per call ─► NodeLink = Iax2Session + NodePttController + NodeAudioEngine(+ IAudioDeviceService)
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
Sources: UI button, Bluetooth, media key, VOX. Modes: **Momentary** (hold) / **Toggle** (latch; UI release ignored; media key always toggles). A **Time-Out Timer** (default 180 s, per-key-up restart) force-unkeys with source `External`. Sends `AST_CONTROL_KEY` (0x0C) / `UNKEY` (0x0D) through the session; `SetPttStateAsync` is idempotent and thread-safe. Disposal unkeys.

### 4.4 Audio pipeline (Core/Audio)
- **Format:** 8000 Hz, 16-bit, mono, 20 ms frames (160 samples) everywhere in Core (`AudioFormat`).
- **`IAudioDeviceService`** (implemented per platform): `StartCapture(Action<ReadOnlyMemory<short>>)`, `StopCapture`, `StartPlayback`, `StopPlayback`, `EnqueuePlaybackPcm`, `BufferedPlayback`, `ErrorOccurred`. Capture callbacks arrive on the driver's audio thread with arbitrary block sizes.
- **`NodeAudioEngine`:** capture → `PcmFramer` (exact 160-sample frames) → mic gain → level meter → (only while PTT keyed) µ-law → bounded drop-oldest queue (8) → `Session.SendAudioFrameAsync`. Receive: `VoiceFrameReceived` → µ-law decode → `JitterBuffer` → 20 ms playout clock (5 ms timer, catch-up, stall protection) → device. Skips frames if the device backlog exceeds 200 ms. `KeepAudioPipelineAlive` writes zero PCM during silence (**iOS keeps its audio session/background execution alive this way**).
- **`JitterBuffer`:** timestamp-keyed (16-bit wrap safe), prime depth 3 frames (60 ms), capacity 25, reorder, drop late/duplicate, conceal up to 3 lost frames by repeating the last frame with halving gain, re-prime after idle; thread-safe.
- **`PcmResampler`:** streaming area-averaging resampler to 8 kHz (used on iOS capture; hardware is usually 44.1/48 kHz). **`AudioLevelMeter`:** RMS/peak with attack/release ballistics; UI maps RMS −60…0 dBFS to 0…1.
- **Transmit voice clean-up (`MicProcessor`, added 2026-11-05 after the first on-air test sounded "mostly clear"):** G.711 does nothing to the signal (8 kHz sampling + log quantisation), and the first live test sent the raw mic at only −28 dBFS peak. Per 20 ms frame, after `MicGain` and before the VU meter/encoder (`NodeAudioEngineOptions.MicProcessing`, driven by the `MicProcessing` setting, default **on**, checkbox in Settings): (1) 2nd-order Butterworth **high-pass at 300 Hz**; (2) slow **AGC** towards −21 dBFS RMS (≈ −6 dBFS speech peaks): gain range −12…+24 dB, attack 0.5/frame, release 0.03/frame (~0.7 s), **gate at −50 dBFS** — pauses/noise never raise the gain and it drifts back to 0 dB (0.02/frame) so hiss is not left boosted; gain is ramped across each frame (no clicks); (3) **soft limiter** (tanh knee from 70% of a 0.89 FS ceiling) instead of hard clipping. Unit-tested (rumble −18 dB+, 1 kHz within 1.5 dB, quiet voice lifted to target, loud voice tamed under the ceiling, pauses not amplified, silence stays silence, ramped gain). **Not yet confirmed by ear on air** (A/B with the owner pending).
- **Voice timestamps (`Iax2Session.NextVoiceTimestamp`):** mini-frame timestamps used to be the wall clock at send time, which measured live as 16–24 ms spacing around a nominal 20. Now each voice frame is stamped previous + its own duration (20 ms), nudged ≤ 1 ms/frame towards the wall clock when it is > 5 ms away (so drift never accumulates), and restarted from the wall clock after a gap > 100 ms or at the start of an over. Unit-tested (jitter → 19–21 ms spacing, 1 % clock drift stays within 8 ms over 60 s, gap resync, 16-bit wrap).
- **Known behaviour:** capture runs for the *whole* connection (the mic is open while linked), not only while keyed.

### 4.5 `NodeLink` and `NodeRuntime` (Core/Link)
- **`NodeLink`** = one call: `Iax2Session` + `NodePttController` + `NodeAudioEngine` + its device. Logs in as IAX user **`radio`** with `CALLING_NUMBER` = local node (see §5.4). Audio starts when the session reaches `Connected` (off the receive thread). `WasConnected` records whether it ever connected.
- **`NodeRuntime`** is the UI-toolkit-independent coordinator. API: `StartAsync`, `ApplyConfigAsync`, `ConnectAsync(targetNode)`, `DisconnectAsync`, `Link`, `Config`, registration state (`IsRegistered`, `RegistrationStatus`, `PerceivedAddress`), and events `RegistrationChanged`, `LinkChanged`, `LinkActiveChanged(bool,node)`, `ReconnectChanged(ReconnectStatus)`, `ErrorOccurred`, `Notice`. Saving settings (via `NodeConfigService.Changed`) re-applies configuration and (re)starts registration automatically.
- **`LinkActiveChanged`** means "a node link is *wanted*" — it stays `true` across automatic reconnects so platform heads don't tear down background services mid-recovery. Android uses it to start/stop the foreground service.

### 4.6 Auto-reconnect and network handoff (spec of record)
**Auto-reconnect** (setting `AutoReconnect`, default on) re-dials when an **established** link is lost:

| Link ended because… | Re-dial? |
|---|---|
| Keep-alive watchdog / retransmit limit / INVAL (`LinkLost`) **and** the link had been `Connected` | **Yes**, with back-off |
| `LinkLost` but the call was **never answered** | No — error "No response from node N" |
| Remote hung up **within `EarlyHangupWindow` (3 s) of ACCEPT** (`RemoteHangup`, `NodeLink.ConnectedDuration` below the window) | **Yes**, back-off `InitialDelay`×`Factor`^(n−1) capped at `MaxDelay`, up to `EarlyHangupMaxRetries` (10 ≈ 6 min); notice "Node N hung up straight after accepting; it may not know this node yet. Retrying in X s (retry n of 10)"; after the limit error "Node N keeps hanging up right after accepting the link…" and stop. Why: live-verified that an ASL3 node accepts, then app_rpt hangs up ~90 ms later ("Reported node N cannot be found") until its own node list has learned this node (minutes after registering or after an address change). Needs `AutoReconnect` on. The counter resets on a new user Connect and when a link stays up past the window. |
| Remote hung up later than that (`RemoteHangup`) | No — notice "Node N disconnected" |
| Remote rejected / busy | No — error |
| User pressed Disconnect | No (also cancels any reconnect cycle) |
| `AutoReconnect` off | No — error "Link to node N lost" |

Back-off (`ReconnectPolicy`): first retry after `InitialDelay` 2 s (immediately after a network change), ×`Factor` 2 per failure, capped at `MaxDelay` 60 s, each attempt must reach `Connected` within `AttemptTimeout` 12 s, `MaxAttempts` null (unlimited) by default. While there is no usable network the loop waits (re-checking every 5 s or on any network event) instead of burning attempts. Status is published as `ReconnectStatus(Active, Attempt, NextAttemptIn, Message)`.

**Network handoff:** `INetworkMonitor` (default `SystemNetworkMonitor`: `NetworkChange` + `NetworkInterface`) reports changes; events are **debounced 1.5 s** and compared by *signature* (set of up, non-loopback, non-tunnel, non-link-local IPv4 addresses) so DHCP renewals are ignored. On a real change: (1) **re-register immediately** (so the registry/remote nodes learn the new address; waits up to 5 s), (2) if a link was established, end it and **re-dial immediately** from the new address (an IAX2 call is bound to the source address and cannot migrate), (3) if the network vanished, wait for it to return. Where the platform restricts network enumeration (recent Android) `SystemNetworkMonitor` degrades to "assume available, signature unknown" and the keep-alive watchdog still catches the loss (~45 s). Android uses `AndroidNetworkMonitor` (ConnectivityManager callback) instead.

### 4.7 Settings, secrets, registration, resolution
- **`NodeSettings`** (JSON, `%LOCALAPPDATA%\NodeX\settings.json`, atomic write, corrupt file kept as `.corrupt`): `NodeNumber`, `Callsign`, `DefaultTargetNode`, `FavoriteNodes`, `RegistrationUrl`, `IaxPort`, `RegisterOnStartup`, `AutoReconnect`, `InputDeviceId`/`OutputDeviceId` (reserved, not yet used by any backend), `MicGain`, `MicProcessing` (default true), `TimeOutTimerSeconds`, `TogglePtt`. **The password is never in this file.**
- **`NodeSettingsValidator`:** node number 3–7 digits; password required, ≤79 chars (ASL buffer is 80); callsign `[A-Za-z0-9]{1,6}(/[A-Za-z0-9]{1,6})?`; https registration URL; port 1–65535; mic gain 0.1–8; TOT 10–600 s; favourites must be node numbers. Normalisation trims, upper-cases the callsign, de-duplicates favourites.
- **`NodeConfigService`:** validates, then writes the **password to `ISecretStore` first**, then settings; raises `Changed`. Invalid input writes nothing.
- **Secret storage per platform:** Windows **DPAPI** (`CurrentUser`); Linux/macOS AES-GCM with a random key in a 0600 file (weaker than a keyring — documented); Android **Keystore** AES-256-GCM key + ciphertext in private SharedPreferences; iOS **Keychain** with `AfterFirstUnlockThisDeviceOnly` (readable in the background with the screen locked, not migrated to backups/other devices). An unreadable secret is treated as "not stored" (user re-enters it).
- **`AslRegistrationService`:** see §5.1. Exposes `PerceivedAddress`, `PerceivedPort`, `ServerRefresh`; interval = configured (60 s) but never longer than the server's `refresh`.
- **`NodeResolution`:** static overrides → 15-minute cache → ASL stats REST lookup → DNS A record (`<n>.nodes.allstarlink.org`, then legacy `node<n>.allstarlink.org`; IPv4 preferred). Injectable HTTP/DNS/clock for tests.

### 4.8 UI layer
- **Palette** (App.axaml, dark): `ConsoleBackgroundBrush #0D1117`, `CardBackgroundBrush #161B22`, `CardBorderBrush #30363D`, `HeaderBackgroundBrush #1F242C`, `TxRedBrush #EF4444`, `RxGreenBrush #10B981`, `AmberAccentBrush #F59E0B`, `CyanAccentBrush #06B6D4`, text `#F0F6FC` / `#8B949E` / `#484F58`. Reuse these via `StaticResource`; do not hard-code new colours.
- **`MainView`:** header (branding, station callsign/node, ASL-registered badge, BLE badge [static], **Settings** button), transceiver card (state badge, latency, node display, RX/TX indicators, real VU meters, TOT bar), PTT button (press/hold handled in code-behind with tunnelled pointer events; toggle via click), connect card (node box, Connect/Disconnect, favourites), DTMF keypad + three function macros, event log. The **settings overlay** is a full-screen `Border` bound to `IsSettingsOpen` containing `SettingsView` — chosen over a separate window so it works on phones.
- **Responsive layout (`MainView.axaml.cs`):** one set of named cards (`HeroCard`, `ConnectCard`, `RxTxRow`, `MetersCard`, `PttCard`, `DtmfCard`, `LogCard`) is re-parented by width — **no separate mobile view, so bindings are shared**. Width < `NarrowBreakpoint` (720): single scrolling column ordered for operating (hero → connect → RX/TX → meters → keypad → log) and the **PTT card is moved into `PttDock`** (bottom row of the root grid, always visible, full-width 96 px button, thumb reach). Width ≥ 720: left column hero/RX-TX/meters/PTT, right column (360 px) connect/DTMF/log. The header has its **own breakpoint** (`CompactHeaderBreakpoint` 920): below it the header becomes rows (brand + Settings / station / badges). The root gets the `narrow` class, which enforces ≥ 44 px button/text-box heights and larger keypad keys. `MainWindow.MinWidth` is 340 so the phone layout can be exercised on desktop. **Gotcha:** when switching to fewer grid columns every child's `Grid.Column` must be ≤ the new column count (a stale index in a hidden child crashed Avalonia's grid measure).
- **PTT button face:** the `Button` is transparent and the visible face is the `PttFace` Border inside it (classes `keyed` toggled by `IsTransmitting`); styling the Fluent template presenter did not take effect, and this also prevents hover/press from overriding the red keyed state. The keyed (red) state has **not been seen on screen yet** (needs a live link).
- **`MainViewModel`:** thin adapter. All state changes from Core threads are marshalled with `OnUi(...)` (`Dispatcher.UIThread`); audio levels are stored in volatile fields and applied by a 100 ms `DispatcherTimer`. Opens Settings automatically on first run. Reconnect status drives the badge ("RECONNECTING (n)") and keeps Disconnect enabled so the user can cancel.
- **DTMF macros:** `*3<node>` (link), `*1<node>` (unlink) using the Connect box's node, `*70` (status); digits sent one per 120 ms via `Session.SendDtmfAsync`.
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

### 5.4 How a node-to-node link logs in (from ASL `iax.conf` docs; **unverified against a live node**)
Incoming node connections land on the IAX user **`radio`** (`[radio]`: `type=user`, `context=radio-secure`, **no secret**); authentication happens afterwards in the `radio-secure` dialplan via `app_rpt`, which validates the calling node (my inference: against the registry/DNS, hence the importance of registration). Therefore the link must use **Username `radio`**, `CALLING_NUMBER` = our node number, **no password**. The node **password is for registration only** and is never sent to remote nodes (a test asserts this). `[allstar-public]` (md5, secret `allstar`) and `[allstar-sys]` (RSA) are different, unused paths.

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
- **Phone layout:** implemented (§4.8). **Still to verify/do on real devices:** iOS safe-area insets (notch/home indicator — Avalonia lays out inside the safe area by default, unconfirmed), Android system-bar/gesture insets, soft-keyboard behaviour when typing a node number (the PTT dock should not jump or cover the field), landscape phones (≥ 720 px wide gets the two-column desktop layout, which is probably too dense at phone heights), tablets, display scaling, and haptic feedback on PTT press (not implemented).
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

`tests/NodeX.Core.Tests` (xUnit, **306 tests**, ~5 s). It references Core **and** `NodeX.UI` (view-model tests). `MainViewModel` itself is untested (needs an Avalonia dispatcher).

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

1. **Live-node validation** — connect to a real ASL node you control: verify call-token handshake, `radio` login, registration, keying, audio both ways, and that a re-dial after handoff is accepted (the remote may still hold the old call until it times out — check behaviour; add a delay/DTMF-free reconnect strategy if it rejects duplicates). Capture packets (Wireshark IAX2 dissector) and compare with §5. Review app_rpt link text protocol (§14).
2. ~~Phone-friendly layout~~ — **done** (§4.8, verified by resizing the desktop app). Remaining: confirm on real phones/tablets and fix landscape-phone density, insets and keyboard behaviour (§9).
3. **Bluetooth PTT**: implement `IBluetoothPttService` for Android (BLE GATT), iOS (CoreBluetooth), desktop (Windows BLE/`InTheHand`/WinRT), plus the Learn wizard UI and media-key fallback; wire status into the BLE badge.
4. **Cross-platform desktop audio** (Linux/macOS): add a backend (spec originally named SoundFlow/MiniAudio; OpenAL/PortAudio are alternatives) behind `IAudioDeviceService`; audio-device pickers for all platforms using `InputDeviceId`/`OutputDeviceId`.
5. **Open the mic only while keyed (or on a short hold-off)** to improve privacy/battery; keep playback pipeline alive.
6. **Android connectivity monitor** (ConnectivityManager callbacks) implementing `INetworkMonitor`; battery-optimisation prompt; permission-denied UX.
7. **Device testing** on a physical Android phone and iPhone (background audio for ≥1 h screen-off, Wi-Fi↔cellular handoff, Bluetooth headset).
8. **Housekeeping:** `git init`, `global.json`, delete `NodeX.UI.Browser`, CI (Windows build + test; macOS runner for iOS signing), `.editorconfig`.
9. **Nice to have:** parse ASL status/linked-node text frames into a "linked nodes" panel; bind configured `IaxPort` to the socket; IPv6; additional codecs; inbound mode over VPN.

---

## 14. Unverified assumptions & risks (check before relying)

- **Link login as `radio` with no password** and acceptance by real nodes (§5.4).
- **Empty-CALLTOKEN-IE-on-first-NEW** behaviour (from Asterisk call-token docs, not RFC 5456).
- **`NodeResolution` REST schema — VERIFIED & FIXED 2026-11-05 against the live service.** The inherited guess (`/api/node/<n>` → `data.ip`) was wrong (404). Real endpoint: `GET https://stats.allstarlink.org/api/stats/<n>` (docs: allstarlink.github.io/developers/api; 30 req/min/IP, cache results). The node's own record is the **top-level `node` object** (`ipaddr`, `port`, `callsign`, `node_frequency`, `Status`, ...); `stats.data.linkedNodes[]` describes its links and must not be used. Checked with node 2530 → 144.202.112.210:4569, identical to DNS `2530.nodes.allstarlink.org` A record (also verified live). Legacy `node<n>.allstarlink.org` returned no data (kept as a harmless last fallback). Nodes that don't report stats return 404 and fall through to DNS.

### Live-validation tool (`tools/NodeX.LiveCheck`)
Console harness driving the real Core engine with a decoded packet trace (`Iax2Session.PacketTrace` tap; independent frame decoder). Commands: `resolve <node>` (read-only), `register` (env `NODEX_NODE`, `NODEX_PASSWORD`), `link <target> [--ip ip[:port]] [--listen s] [--key s] [--dtmf digits] [--trace file]`. Nothing is keyed unless `--key` is given; password only via env var. Run: `dotnet %LOCALAPPDATA%\NodeX\build\bin\NodeX.LiveCheck\Debug\net10.0\NodeX.LiveCheck.dll <command>`. `register` has **not been run yet**.

**First live link result (2026-11-05, owner's cloud node 523800 at 44.240.92.195:4569, dialled from portable node 523802 while UNREGISTERED, listen-only):**
- VERIFIED on the wire: `NEW`(IEs 1 called, 2 calling, 3 name, 9 format, 8 capability, 11 version, 6 username `radio`, 54 CALLTOKEN empty) → server `CALLTOKEN` (IE 54 = `<epoch>?<40-hex>` string) → `NEW` with token → `ACK` + `ACCEPT` (IEs 9 format = ulaw 4, 56 = FORMAT2) → our `ACK` → `Connected` in ~160 ms. So username `radio` with no password is accepted at the IAX layer, and the call-token flow matches §5.3.
- **Remote sent `HANGUP` (IE 42 = 0x00, no text cause) ~90 ms after ACCEPT**; session ended `RemoteHangup`. Likely app_rpt rejecting the caller after the dialplan starts. **Registration VERIFIED live** (`register` → `registered=True`, registry sees 108.65.179.71:4569, server `refresh` = 179 s, status text "Registered as ip:port"). **Re-dial after registering gave the identical result** (ACCEPT then HANGUP IE 42=0x00 after ~90 ms), and the dialling host's public IP equals the registered IP, so "unregistered" and "source IP mismatch" are ruled out. A third dial after the node appeared in the public listing/DNS (`523802.nodes.allstarlink.org` → 108.65.179.71, stats API 200, `callsign='KV4S'`) was again identical, so registry propagation delay is not the (only) cause (523800's own DNS cache could still lag). **LIVE MIC OVER THE NETWORK VERIFIED ON THE SERVER SIDE (2026-11-05 ~23:57 UTC, `LiveCheck link 523800 --mic 6`, owner-approved; relayed to the owner's node 47923):** 6 s keyed with the real Shure mic through `NAudioDeviceService` → G.711 → IAX2: `CONTROL/KEY`, 1 full + 298 mini voice frames (299 mic frames ≈ 6.0 s, peak −28.2 dBFS), `CONTROL/UNKEY`, **0 retransmits, 0 VNAKs**. Polling 523800 (`rpt show variables 523800` over SSH every ~0.4 s) showed **`RPT_TXKEYED` 0 → 1 for ~6.1 s (16 consecutive polls) while we were keyed, then back to 0** (RPT_RXKEYED/ETXKEYED stayed 0), i.e. app_rpt accepted our link keying, keyed its transmit path for exactly our transmission length and unkeyed when we unkeyed. What the owner heard on 47923 is recorded in the next sentence if confirmed; otherwise pending.

**DTMF FUNCTION CODES VERIFIED (2026-11-05 23:41 UTC, `LiveCheck link 523800 --dtmf '*70'`, owner-approved on their own nodes):** NodeX sent `*`, `7`, `0` as full DTMF frames (type 1, subclass = ASCII digit, 120 ms apart). 523800's Asterisk log shows each arriving as `DTMF end '<d>' received ... duration 0 ms` (then Asterisk "emulates" begin/end because the duration is 0 / below its 80 ms minimum — harmless, it works), and app_rpt executed the function: 0.14 s after the last digit it answered with a link TEXT frame **`T 523800 STATUS,523800,0,T523802,T47923,T1997,T1996`** (= `*70` → `ilink,5` system status: node, the node's links; 523802 is now listed as a transceive link). `link_functions = functions` on 523800, so function digits from a link are honoured and `*3/*1/*70` macros in the UI use the right mechanism. No spoken audio came back (status over a link is text, 0 non-silent frames), so nothing was relayed on air. **Observed app_rpt link TEXT messages:** `!NEWKEY1!` (once at connect), `L <T-prefixed linked nodes>` (at connect and every ~30 s), `T <node> STATUS,<node>,<n>,<links...>` (reply to `*70`). NodeX shows them raw; parsing the link list/status for the UI is a possible enhancement. **Possible polish:** Asterisk's DTMF emulation message suggests sending DTMF_BEGIN (frame type 0x0C) + END with a duration ≥ 80 ms instead of END-only; not needed for function codes to work.

**WINDOWS AUDIO BACKEND (`LiveCheck audio`, 2026-11-05; compiles the production `NAudioDeviceService.cs` unchanged; local only, no network, nothing keyed or saved):** capture mechanics VERIFIED on the dev PC — default WaveIn opened at 8 kHz/16-bit/mono, delivered 199 blocks of 160 samples (20 ms) = 3.98 s of audio in 4.14 s wall clock (real-time rate, no drops), playback opened and drained (`IsPlaying`, 400 ms buffer then empty). **Mic capture of real speech VERIFIED:** the first run was silence (−89.5 dBFS; nobody speaking), the rerun with the owner speaking gave **−39.5 dBFS RMS / −16.1 dBFS peak** with the same real-time block cadence (199 × 160 samples in 4.11 s). **G.711 round trip on that speech:** error −75.4 dBFS against signal −39.5 dBFS, i.e. ~36 dB SNR, normal for µ-law. **Speaker output VERIFIED by ear:** `LiveCheck tone` (1 kHz, fade-in −46→−20 dBFS, mic not involved, production `NAudioDeviceService` playback on the system default output) was heard by the owner. Two later mic runs were near silent (−73/−74 dBFS) and their playback was therefore barely audible — most likely the Windows default input changed (headphones plugged in) or the mic level was low; not a playback fault. The quiet runs were the owner not speaking (`LiveCheck miclevels` showed the default mapper and the Shure MV7+ give identical levels; the Virtual Audio Cables read ~−97 dBFS). Second good speech run: **−37.5 dBFS RMS / −22.3 dBFS peak, G.711 round-trip error −73.3 dBFS (~36 dB SNR)**. A further run (−43.8 dBFS RMS, round-trip error −77.7 dBFS) was **heard by the owner: "sounds great"** — so Windows mic → G.711 → speaker audio is VERIFIED for NodeX's own code (local only; the same chain over the network/relay is not yet tested). Input devices on the PC: Shure MV7+, three Virtual Audio Cable lines, MOTIV Mix; the app always uses the system default device (device pickers are backlog item 4).

**LINK WORKS (2026-11-05, after 523800's node list learned 523802): 45 s listen-only session, ended by our clean `HANGUP` (`LocalRequest`).** Verified on the wire, from trace + 523800's console: `ACCEPT` → remote `CONTROL/ANSWER` → unknown control subclass 0x7F (ACKed, ignored) → app_rpt text `!NEWKEY1!` at ~0.75 s, then `L T47923,T1997,T1996` (link list, repeats every ~30 s). **Keep-alive both directions:** our `PING` every 15 s answered by `PONG` (RTT 66–67 ms; the PONG carries ~34 bytes of IEs); remote `PING` every ~21 s answered by our `PONG`; remote `LAGRQ` every 10 s answered by our `LAGRP`. **Audio RX:** remote streams µ-law continuously — one full `VOICE` frame (subclass 0x04, 172 B) then 2,130 mini frames of 160 B over 45 s (~47/s ≈ 95% of the 50/s nominal; content was silence 0xFF — **not** a quality test). No retransmits, VNAKs or INVALs; every sequenced frame was ACKed. **We never answer `!NEWKEY1!`:** 523800's log shows `newkeytimer expired on connected node, setting newkey from RADIO_KEY_NOT_ALLOWED to RADIO_KEY_ALLOWED` ~0.8 s after connect, i.e. app_rpt falls back to allowing keying without the NEWKEY handshake. **Still unverified:** that our `CONTROL/KEY`/`UNKEY` + voice are honoured (needs `--key`), TX audio quality, real mic/speaker audio, DTMF function codes, re-dial from a new IP while the stale call lingers.

**KEYING VERIFIED END-TO-END (2026-11-05 23:19 UTC, trace8.log, run with owner's knowledge):** `CONTROL/KEY` at 0.24 s, 1 full + ~249 mini voice frames (5 s of µ-law silence), `CONTROL/UNKEY` at 5.25 s, no retransmits/VNAKs; 523800 relayed it and **the owner observed their own node 47923 key** (523800 is permanently linked transceive to 47923, 1997, 1996). The KEY was sent ~0.24 s after connect, before 523800's `newkeytimer` expired (~0.9 s), and was still honoured as far as the owner could see, so no extra hold-off appears necessary (the exact moment of the first key-up was not measured). **Safety note:** testing against 523800 keys real RF on 47923 — never run `--key` there without the owner present and aware. Earlier unobserved 22:15:36 UTC keyed test (trace7.log) is likely to have done the same. Server console at verbose 4 shows only bridge join/leave and the newkey-timer warning, not key-up lines.

**RE-DIAL WHILE A STALE CALL EXISTS (2026-11-05 ~23:41 UTC, `LiveCheck redial 523800`, no keying):** call A connected (ANSWER, NEWKEY, link list), was then **abandoned silently** (socket closed, no HANGUP), and call B was dialled 15 ms later from a fresh socket (new source port, same public IP). **B was accepted immediately** (CALLTOKEN → ACCEPT → ANSWER → text → voice in ~2.4 s), ran 20 s with PING/PONG, and ended with a clean HANGUP. 523800 did **not** reject the duplicate from the same node number; its `iax2 show channels` showed A already in `Tx:HANGUP` state seconds later and gone afterwards, i.e. the stale call is cleaned up promptly (cause not determined: new link from the same node replacing the old one, or ICMP port-unreachable). No delay/duplicate-avoidance strategy is needed for the same-IP case. **Still unverified:** a genuinely different public IP (only one IPv4 interface on the dev PC; the registry/remote node list must learn the new address first — remotes appear to store the registered IP, `rpt lookup 523802` → `radio@108.65.179.71:4569/523802,108.65.179.71` — and may reject a caller whose address is not yet the registered one; that was not testable here).

**ROOT CAUSE of the earlier immediate HANGUP, found from 523800's Asterisk console (ASL3 `Asterisk 22.10.1+asl3-3.10.8`)** — the call lands on `[523800@radio-secure]`: "Accepting UNAUTHENTICATED call", `Set(NODENUM=523802)` (taken from our CALLING_NUMBER IE), allowlist/denylist steps pass, then `Rpt(523800)` fails with **`app_rpt.c:7526 rpt_exec: Reported node 523802 cannot be found!!`** and the channel is hung up. So app_rpt validates the caller by looking it up in the **remote node's own local node list**, which refreshes from the registry on a delay (owner: up to ~5 min, sometimes more) — this is §5.4's inference, now confirmed. It is NOT a NodeX protocol bug (handshake, `radio` login, CALLING_NUMBER are all accepted). Implication for the app: a freshly registered/moved node (e.g. after a network handoff to a new IP) can be rejected by remotes until their lists refresh; reconnect logic should treat a quick post-ACCEPT HANGUP as "not yet recognised" and retry with back-off rather than give up. (Earlier note, now superseded:) cause was unknown — needed 523800's Asterisk log during a dial (and its allow/deny or private-node config); the HANGUP carries no text cause.
- Tool bug fixed: connect wait loop must also wait through `CallTokenHandshake`.
- **app_rpt link text protocol:** real ASL links exchange `AST_FRAME_TEXT` messages (link/keying status such as `!!NEWKEY…`-style negotiation and periodic status). NodeX sends only control KEY/UNKEY and ignores/prints text. This may be sufficient or may cause odd behaviour (e.g. remote not recognising keying) — verify against app_rpt source (`rpt_link`/`rpt_manager`) and a packet capture.
- **Reconnect from a new address** being accepted by the remote while it still holds the stale call.
- **Mic-open-while-connected** and iOS background longevity with the zero-PCM keeper (Apple may still suspend the app; needs device testing).
- **Android `NetworkChange`** availability on API 30+ (see §4.6).
- Windows audio path (NAudio `WaveInEvent`/`WaveOutEvent` at 8 kHz) never exercised with real hardware.

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
- **Build:** build output moved out of OneDrive; iOS and Android build on Windows; solution includes all heads and tests.

---

## Appendix: always-connected battery model (2026-11-05, **not device-tested**)
Requirement: once linked, stay linked until the user disconnects, as cheaply as possible on Android and iOS.
- **Mic gating:** `NodeAudioEngineOptions.OpenMicOnlyWhileKeyed` (set by the Android and iOS heads; default off so desktop/tests are unchanged). `IVoiceChannel.TransmittingChanged` (default no-op; raised by `Iax2Session.SetPttAsync`) opens capture on key; capture closes `MicHoldOff` (3 s) after unkey unless re-keyed. The mic input meter is idle while closed. Cost: first ~tens of ms of speech may be clipped while `AudioRecord`/the tap spins up — verify on devices; raise nothing else.
- **UI:** `MainViewModel.SetForeground(bool)` stops/starts the 100 ms meter timer from `IActivatableLifetime` Activated/Deactivated.
- **Android:** `AndroidNetworkMonitor`; one-time battery-exemption prompt.
- **iOS:** the zero-PCM keeper is deliberately **kept**: the remote streams ~47 packets/s even in silence so it rarely writes zeros, but removing it risks background suspension and cannot be judged without a real iPhone. Revisit with a ≥1 h screen-off test.
- **Unavoidable cost:** the remote sends continuous µ-law (≈47 pkt/s) for the life of the link, which keeps the radio active; only a remote-side change could reduce it.
- Test: `OpenMicOnlyWhileKeyed_OpensOnKey_AndClosesAfterHoldOff`.
