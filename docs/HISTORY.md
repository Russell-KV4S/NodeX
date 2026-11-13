# Historical engineering notes

> Archived on 2026-11-11. These are dated observations and superseded proposals, not the current backlog or behavior contract. Use PROJECT_SPEC.md for current behavior. Later observations supersede earlier ones; a compile check is not a hardware test.

## 17. Amateur-radio compliance (decisions; not legal advice)

NodeX links into ASL nodes that key real RF transmitters (the owner's test node 523800 is permanently linked to RF node 47923). Transmitting through such a link is amateur-radio transmission by the control operator of the *remote RF node* and by the person speaking. The spec previously said nothing about this; item 1 is decided; items 2–4 still need an owner decision before NodeX is distributed to anyone else:

1. **Station identification — DECIDED 2026-11-05 (owner); implemented 2026-11-05 except the mobile background notification.** As built: `Core/IdReminder` (injectable `TimeProvider`; clock starts on the first key-up (start of a transmission) when none is running; "I've identified" tapped while still transmitting starts a fresh clock at once (`Acknowledge(transmitting: true)`, 2026-11-11), otherwise the next key-up starts it, becomes *due* after the interval, stays due until acknowledged or the link ends; later key-ups do not restart it; switching the feature off clears it). `MainViewModel` feeds it from `PttStateChanged`, resets it when the link goes away, and shows an amber "Time to identify (say your callsign)" banner with an **I've identified** button in the hero card, plus an `[ID]` activity-log line; it also raises `IdReminderDue` so a platform head can post a notification (not wired yet). Settings screen: "Station ID reminder" card. Original decision text follows. NodeX transmits **nothing but the user's voice**: no automatic/synthesised ID, no beacons, no tones. Instead it offers an **optional ID reminder** that the user can switch on or off in Settings: while linked and after the user has transmitted, a UI prompt (and a mobile notification when backgrounded) reminds them to identify by voice after the configured interval. Planned design: setting `IdReminderEnabled` (bool; default **off** — opt-in, easy to flip) and `IdReminderMinutes` (default 10, validated range e.g. 1–60); the timer runs only while a link is active and restarts when the user identifies (a "I've identified" tap) or a transmission ends after a long gap; it never keys the radio or injects audio. The user remains responsible for meeting the identification rules that apply to them.
2. **Licensing.** Decide whether the app states that a valid amateur licence is required to transmit, and whether `Callsign` is required before keying is enabled. **Callsign DECIDED 2026-11-06 (owner):** a callsign is required unless the user chooses "Listen only"; listen-only turns PTT off but still connects and listens. The settings screen says a licence is needed to transmit and that NodeX does not verify the callsign. Owner decision 2026-11-11: the callsign requirement for transmitting stands, and a plain disclaimer that NodeX does not verify callsigns is enough (README and settings text say so). Still open: store-listing wording. **Superseded 2026-11-12 (owner):** the callsign field and the "Listen only" option were removed entirely; the callsign was display-only and never sent, and an AllStarLink node login is needed for everything. See PROJECT_SPEC.
3. **Third-party / link policy.** Some node owners restrict who may link; NodeX should surface "rejected/busy" clearly (it does) and never retry in a way that looks like abuse (early-hangup retries are capped at 30, with back-off up to 60 s).
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

- **2026-11-06 follow-ups (built, uncommitted):** (1) `DnsPortLookup` reads the SRV (`_iax._udp.<n>.nodes.allstarlink.org`) / TXT (`PT=`) port with raw UDP DNS (live-verified: 4569 for 27339/45192/523800). `NodeResolution` tries listed port → DNS port → 4569 (probe each); the DNS port is a fallback, NOT preferred, because 27339's DNS says 4569 but it answers on its listed 4579. (2) `Iax2Session.SendNewKeyOnAccept` (default on) sends `!NEWKEY1!\0` right after ACCEPT; hub ACKed it, but a held-link/TX test is UNVERIFIED (523802 must be registered, else hubs hang up ~0.1 s after accept, with or without NEWKEY1). (3) `Iax2Registrar` = IAX2 REGREQ/REGAUTH(MD5)/REGACK over UDP from its own socket (CALLTOKEN echoed); opt-in setting `UdpRegistration` (default off, no UI yet); loopback-tested only, never tried against register.allstarlink.org. It is a separate socket from the call sockets, so the registry would record that socket's port, not 4569.

## Appendix: cloud relay (Zello-style) — considered, **probably not pursued** (2026-11-08)
Idea: a small server (Vultr $5–10 or AWS) holds each user's IAX2 link to AllStar and talks WebSocket/TLS + push to the phone, so the phone can sleep when idle and be woken by push (Zello's battery/background model). Still AllStar, not a separate network. **Owner decision: maybe, probably not, because of the concerns below.** Nothing built.
- **Credentials:** the server must hold every user's ASL node number and password (encryption at rest, breach plan, Play Data Safety/privacy policy). Callsign ownership would need verifying.
- **Single point of failure:** one outage drops all users; DDoS null-routing on the host; direct mode would have to stay as a fallback.
- **Shared IP:** all users reach AllStar from one cloud IP — possible registration rate limits (unverified) and operators blocking cloud ranges would affect everyone at once.
- **Cost/scale (estimates, unmeasured):** µ-law ≈80–90 kbps per active link; ~1–2 TB/month on a small Vultr plan ≈ 150–300 heavy users; AWS egress ≈ $0.09/GB, so Opus to the phone is preferable. 1 vCPU ≈ 50–100 concurrent active streams with transcoding.
- **Push wake latency:** 1–3 s or more, so the server must buffer the start of each transmission; iOS push needs the paid Apple account that is shelved.
- **Upside:** a public IP makes inbound links possible despite carrier NAT.
- **Do first instead (no server):** Android foreground service + smarter keep-alive and the AlarmManager heartbeat fallback (see the stay-registered appendix). Only revisit a relay if idle battery drain remains a real problem after device testing.

## Appendix: Android Auto — Phase 1 **implemented, untested on a head unit** (2026-11-08)
**Status (2026-11-08, later):** Phase 1 is coded and the Android head builds; the merged manifest has the service and the `com.google.android.gms.car.application` meta-data. **Not run on the Desktop Head Unit, a phone or a car.** Phase 2 (transmit) is not started.

**What was built**
- `NodeX.Core/Link/CarBrowse.cs` (+ `CarBrowseTests`): favourites to browse rows, Next/Previous with wrap, voice query matching, "replace only the car's previous node".
- `NodeRuntime.StartAsync` is now idempotent and awaitable by every caller (cold start from the service while the UI starts later).
- `Services/NodeXMediaBrowserService.cs`: framework `MediaBrowserService` + `MediaSession`; caller allow-list in `OnGetRoot` (Android Auto, Assistant, System UI, system, uid-checked); initial state Stopped; Play/Pause/Stop/PlayFromMediaId/PlayFromSearch/Next/Previous; Pause = hang up; error state with a phone-setup message; audio focus (transient loss mutes, permanent loss hangs up the car's node, regain restores only what it muted). A link already up when the car connects is adopted as now-playing (**owner confirmed 2026-11-08**: show it as now-playing; the car session does not start its audio, so MA-1 autoplay is not triggered by adoption).
- `Services/CarSession.cs`: flag set while a car session owns audio. It makes the `AudioTrack` use `Media` usage, skips Bluetooth SCO, and drops the `microphone` foreground type in `NodeForegroundService`.
- `Application.OnCreate` now also configures services (guarded), so the service can start the process with no Activity.
- Nothing in the car path touches `NodePttController`, `HardwarePtt` or the transmit choice, and `OnMediaButtonEvent` is not overridden.

**Requirements re-checked 2026-11-08** (developer.android.com car media + quality pages): `automotive_app_desc` with `<uses name="media"/>` and the meta-data are unchanged. New or newly noted: the docs now describe `MediaBrowserServiceCompat` / Media3 `MediaLibraryService` and never the framework classes (we use the framework ones; untested whether that is accepted, fall back to the AndroidX binding if the DHU shows problems). Required default actions include Pause. Quality criteria that apply: MA-1 no autoplay on startup (initial state Stopped, no play on bind), VC-1 Gemini/Assistant voice must work, DR-1/2/3 buttons answer within 2 s and launch/content load within 10 s (the first dial can wait on the registration settle delay, so Buffering is shown at once), EP-2 restore state on relaunch (not done), VD-2/VD-3 white icon set and colours the system can adjust (**no car media icon provided yet**), GB-1 disabled controls must be inert. Error state must carry a user-facing message, and say so when the phone must be used. The September 2026 note says Games became generally available; the templated-media/calling restrictions in the old text below were not re-verified.

**Still to do:** run on the DHU and a car; confirm voice (`onPlayFromSearch`) with Assistant/Gemini; a white monochrome media icon; check `StartForegroundService` is allowed from a car command with the app never opened; EP-2 state restore; tests for the service itself (only `CarBrowse` is unit tested).

*Original proposal and analysis (written earlier 2026-11-08, before implementation):*
Google's rules below were read from developer.android.com on 2026-11-08 and change often. Code references were read the same day.

**Goal:** use NodeX in the car from the Android Auto screen. Phase 1 is listen-only. Transmit is a separate, optional Phase 2.

### Platform constraints
- Android Auto only accepts listed app categories. There is no push-to-talk or radio-link category. In-app audio while driving is limited to media apps and navigation voice guidance.
- **Media** (`MediaBrowserService` + `MediaSession`; the category description includes radio): all Play tracks. Android Auto draws the browse and now-playing screens; the app supplies no car UI.
- **Templated media** (Car App Library): early-access program; internal and closed testing tracks only.
- **Calling** (Telecom Jetpack + Car App Library): beta; internal and closed testing only; builds on open or production tracks are rejected. Telecom must be used at all times, not just in the car. The docs do not mention push-to-talk or group calling.
- **Car microphone:** `CarAudioRecord` (Car App Library, car API level 5, `RECORD_AUDIO`) delivers 16 kHz L16, so it needs resampling to NodeX's 8 kHz. It requires audio focus, and `read` returns -1 when the user dismisses the mic on the car screen. It is not available to a plain `MediaBrowserService` app.
- **.NET:** `Xamarin.AndroidX.Car.App.App` 1.7.0.4 supports `net10.0-android` (needed for Phase 2 options B and C only). Some templated-media requirements refer to Car App Library 1.9, so check the binding is new enough. `Android.Service.Media.MediaBrowserService` and `Android.Media.Session.MediaSession` are in `Mono.Android`. Phase 1 may need no new package; if Android Auto does not work with the framework classes, use the AndroidX media binding.

### Phase 1: listen-only media app
**Car behaviour**
| Surface | Behaviour |
|---|---|
| Browse root | One playable item per `NodeSettings.FavoriteNodes`, titled from `FavoriteLabels` or else the node number. |
| No favorites | One non-playable item telling the user to add favorites on the phone. |
| Select a favorite | `NodeRuntime.ConnectAsync(node)`. It becomes the now-playing node. |
| Select another favorite | Connect it, then `DisconnectAsync(previous)` for the previous now-playing node only. Links started on the phone are left alone. **Proposed default; owner to confirm.** |
| Link already up when the car connects | Show it as now playing (`Runtime.Link` if set, else the first link). |
| Stop | `DisconnectAsync(now-playing node)`. |
| Next / Previous | Move through favorites, same as selecting one. |
| Playback state | Connecting or reconnecting (§4.6) = buffering; connected = playing with unknown position; no link = stopped. No pause, seek or position. |
| Metadata | Title = label or node number; subtitle = connection state, later receiving or last-heard time. A talker's callsign needs link TEXT frame parsing (§13 item 1) and is out of scope. |
| Voice | `onPlayFromSearch`: match a favorite label or node number; an empty query plays the last-used favorite. Confirm against the current voice-actions docs. |
| Not configured or registration failed | Playback error with a short message to finish setup on the phone. No forms or text entry on the car screen, so Find-a-node and Settings stay phone-only. |

**Never in Phase 1:** nothing reachable from the car may key the transmitter. `MediaSession` callbacks must not touch `NodePttController` or `HardwarePtt`.

**Implementation**
1. New `Services/NodeXMediaBrowserService.cs` in the Android head: exported, intent filter `android.media.browse.MediaBrowserService`, owns the `MediaSession`, and follows `NodeRuntime` events (`LinkAdded`, `LinkRemoved`, `ConnectionsChanged`, session state). Validate the caller in `OnGetRoot`.
2. Add `Resources/xml/automotive_app_desc.xml` (`<automotiveApp><uses name="media"/></automotiveApp>`) and application meta-data `com.google.android.gms.car.application` pointing at it.
3. Provide a car-appropriate monochrome media icon.
4. Keep NodeX behaviour in `NodeX.Core` where practical (§12): for example a favorite-to-browse-item mapping and the "replace now-playing node" rule, with unit tests. Only Android bindings go in the head.

**Known code problems to solve**
- **Cold start:** Android Auto can bind the service with no Activity. Today `Runtime.StartAsync` and the settings load are reached only from `MainViewModel.InitializeAsync`, created in `App.OnFrameworkInitializationCompleted`. Whether that runs without an Activity is unverified. Move startup to an idempotent call in `AppServices` that the service and view model can both use.
- **Foreground service:** `NodeForegroundService.TypesFor` adds `microphone` whenever `RECORD_AUDIO` is granted. Android 14+ restricts starting microphone-type services from the background, and `Apply` handles a refusal by shutting the service down. A car-started link must use `mediaPlayback` without `microphone`. Also confirm that `EnsureStarted` is permitted when started from a car media command.
- **Audio path:** `AndroidAudioDeviceService` plays with `AudioUsageKind.VoiceCommunication`. `AndroidAudioRoute` sets `Mode.InCommunication` and defaults to Bluetooth SCO or the earpiece. `NodeForegroundService.Apply` starts SCO when linked if a Bluetooth headset is present, and a car's hands-free link may count as one. Car listening needs usage `Media`, content type `Speech`, normal audio mode and no SCO. The `AudioTrack` usage is fixed when it is created, so a link already up when the car connects or disconnects needs its track rebuilt.
- **Audio focus:** no focus handling exists today. Request `AUDIOFOCUS_GAIN` on connect and abandon it on stop. On transient loss (navigation prompt, phone call), duck or mute but keep the link up. On permanent loss, define the behaviour during the first head-unit test.
- **Receive state:** `IsReceiving` and last-heard live in `LinkItemViewModel`. A car subtitle needs them exposed from Core.

**Acceptance criteria (Phase 1)**
- NodeX appears in the Android Auto launcher on the Desktop Head Unit, then in a real car.
- With the phone locked and the app never opened since boot, a favorite can be selected and heard through the car speakers.
- Stop drops the link and the foreground notification returns to its idle state.
- A navigation prompt ducks or interrupts the audio, and playback resumes without redialling.
- A phone call pauses audio; the link recovers after the call.
- Wi-Fi to cellular handoff shows buffering and resumes (§4.6).
- No car action can transmit, confirmed by a unit test on the controller path and by node logs.
- Non-car phone and Bluetooth headset audio are unchanged.
- Empty favorites and incomplete settings show the message states rather than a blank screen or crash.

**Testing:** Google's Desktop Head Unit on the PC first (`adb forward tcp:5277 tcp:5277`, Android Auto developer mode, "Unknown sources" for sideloaded debug builds), then the Play internal track and a real car.

### Phase 2 option A: **implemented, untested** (2026-11-08)
Owner asked for it ("build it all"). Settings (Android only, both **off by default**): `CarPtt` (Talk button on the car now-playing screen, a `MediaSession` custom action `nodex.talk`) and `CarPttWheel` (`CarWheelTalk`: Off, Next or Back; the chosen steering-wheel button toggles talk instead of skipping favourites and the other still skips. Originally play/pause, then Next only, then a choice 2026-11-08 because the owner's car wheel only has Back/Next). Rules live in `NodeX.Core/Link/CarTransmit.cs` (`Decide`, unit tested in `CarTransmitTests`): toggle only; keys only when the car's now-playing node is connected AND is the chosen transmit node (the car never changes the transmit node, a mismatch shows a message); `TransmitPolicy` (listen-only, mic denied) and the time-out timer still apply in `NodePttController` (new `PttSource.Car`); unkey is never refused. Every car key is released when the car session ends, audio focus is lost (nav prompt, call), the stop command runs or the service is destroyed. Key and unkey play a tone (`ToneGenerator`) and the car subtitle shows TRANSMITTING; car actions are written to the diagnostic log (category `car`). Never keys on connect, reconnect or head-unit attach.
**Microphone caveat:** a car session normally omits the `microphone` foreground type (Android 14+ refuses it for a service started from the background). With `CarPtt` on, it is requested; if Android refuses, `NodeForegroundService` falls back to listen-only and `CarSession.MicrophoneUsable` stays false, so Talk refuses with a message instead of sending silence. Unverified: whether it is granted when Android Auto starts the app cold, and whether `AudioRecord` gets the phone or the car microphone during projection.
**Who is talking (RX) in the car (2026-11-08):** the per-node RX rule from §18 (KEY frame OR a voice frame within 500 ms) now lives in Core as `RxActivity` (the Radio screen still has its own copy in `MainViewModel`; they could be merged later). The car service follows every link with it and shows "Hearing <label or node number>" in the now-playing subtitle (several names if several nodes are heard), refreshed at most every 2 s; TRANSMITTING and refusal messages take priority. A talker's callsign is still out of scope (needs link TEXT parsing). Unverified: whether Android Auto announces each subtitle change as a notification (if it does, switch to a quieter display); `RxActivity` has no unit test (needs a live session).
**Last-heard age fix (2026-11-08):** a Radio row could stay on "Last heard 3:03 PM (just now)" for minutes (seen on a phone at 3:07). The age text only refreshed from the meter timer, which is stopped in the background and runs at Background priority. A separate 1 s Normal-priority `_ageTimer` now refreshes every row, and it is also refreshed immediately when the app returns to the foreground. `LinkItemViewModel.AgoText` is unit tested (`LastHeardAgeTests`: "just now" under 60 s, then "N min ago"). The root cause on the phone is inferred, not reproduced: **unverified on a device**.
**Not built, on purpose:** option B (templated media with `CarAudioRecord`) and C (calling category) are early-access/beta, limited to internal and closed tracks, need Google enrolment and the Car App Library binding, and cannot be verified without a head unit. Revisit only after A has been tried in a car. Station-ID reminders are not wired to the car (§17 reminder is phone UI only).

### Phase 2 options as originally proposed
| Option | Approach | Limits |
|---|---|---|
| **A (try first)** | Map a `MediaSession` command from the steering wheel to `HandleButtonAsync` on `Runtime.Link.Ptt`, with a new `PttSource`. | Toggle only (press to key, press to unkey). Phone or Bluetooth mic unless routed otherwise. Google may object to this in a media app, so internal track only until that is known. |
| **B** | Templated media with an on-screen PTT action and `CarAudioRecord`. | Early access; internal and closed tracks only. |
| **C** | Calling category, treating a link as a call and mute as PTT. | Beta; internal and closed tracks only. Telecom integration at all times. Poor fit for PTT. |

**Safety requirements for any Phase 2 option:** off by default behind an explicit setting; honour `TransmitPolicy` and the multi-node transmit rules (only the chosen transmit node; never redirect to another node); enforce the existing time-out timer (`TimeOutTimerLimit`) whenever keyed from the car; give an audible key and unkey indication; never key on connect, reconnect, or head-unit attach or detach. The owner's 523800 relays to RF node 47923, so check links and warn before any keyed test. Review §17 station-ID behaviour for hands-free use.

### Open questions
- Will Google approve a ham-link app in the media category for production? Listen-only is the best case; it is unconfirmed.
- Does a third-party media app's `AudioRecord` get the phone mic or the car mic during Android Auto? Only relevant to Phase 2 option A.
- Does the transmit timing and audio quality hold up with the head-unit audio buffering? Measure it.

## Appendix: branding (2026-11-06)
Logo (**owner-supplied navy X with amber outline and traces**, `branding/screen.png`; supersedes the earlier AI blue-X mark) applied: desktop window/exe icon (`Assets/nodex.ico`), header logo (`Assets/nodex-mark.png`), Android adaptive + monochrome launcher icons, notification small icon, splash, browser favicon. Sources, palette and the regeneration script (`build_assets.py`) are in `branding/` (see `branding/BRAND.md`). Android build and desktop launch verified; Android icon/splash **not seen on a device**. iOS has no app-icon asset catalogue yet.

Later the same day the logo was replaced by the owner's own artwork: `branding/logo/nodex-logo-light.png` (as uploaded, transparent) and `nodex-logo-dark.png` (same artwork, navy fill lifted to #3F6FE0 so it reads on the dark UI). `build_assets.py` loads those two PNGs for the "dark"/"light" marks (the mono SVG is still used for the Android themed and notification icons). App/launcher/Windows/favicon icon was first the light logo on a pale blue #E8EEFC tile, **superseded later 2026-11-06 by the dark #0D1117 tile with the dark logo** (owner request; `ic_launcher_background` = #0D1117); header logo and splash use the dark logo on the dark UI. The app theme itself stays dark. If Windows still shows an old desktop-shortcut icon, that is the Windows icon cache (`ie4uinit.exe -ClearIconCache`, restart Explorer) or a shortcut pointing elsewhere; the deployed exe embeds the new icon. Not seen on a phone.

## 18. Troubleshooting log (added 2026-11-06)

- `NodeX.Core/Diagnostics/DiagnosticLog` (static `Default`, configured in `AppServices.Initialize` to `<app data>/NodeX/logs`): rolling file log, `nodex.log` + 2 older (512 KB each), lines `yyyy-MM-dd HH:mm:ss.fff LEVEL [category] text`, secrets redacted (`password/secret/token/authorization=...`), never throws.
- `Info` lines always written (every on-screen event-log line, registration changes, network changes, call ends, reconnect attempts). `Detail` lines only when setting `DetailedLogging` is on (network events, resolve/registration timings, IAX session states, attempt starts).
- Settings > Troubleshooting: "Detailed logging" checkbox and "Send logs…" (Android share sheet with the log as plain text, capped at 120 000 chars; desktop shows the file in the file manager; hidden where no `AppServices.LogSharer`). The log contains node number, callsign and IP addresses, never the password.
- Network handoff: `ReconnectPolicy.NetworkChangeDebounce` lowered 1.5 s to 0.5 s; affected links are now hung up in parallel. Not changed: the call is still dropped and re-dialled on a network change (IAX2 ties a call to the source address).
- UI: Radio screen header shows an amber LISTEN ONLY chip whenever transmitting is off; bottom bar has a 5th Settings cog; Radio tab icon is a vector handheld transceiver; the "loudspeaker by default" setting is always listed (disabled off-Android).
- Icons: dark tile (#0D1117) with the dark-variant logo everywhere (`branding/build_assets.py`).
- Unverified: all of the above on a real phone; the share sheet; the new nav layout/icon visually.
- Navigation (2026-11-06 night): Back closes an open panel (Find a node / Settings), then steps back through the phone tabs (history, falling back to Radio), and from Radio sends the app to the background (`AppServices.BackHandler` + `TabBackHandler`). The header "Menu" is gone: the header now has Exit (hidden on iOS) and, on wide layouts only, Settings; phones use the bottom bar. `MainActivity` now sets `HardwareKeysSupported`/`SpeakerphoneSetter`/`LogSharer` before `base.OnCreate` so Settings sees them (phone-button mapper was missing); Settings also re-announces them on open. Unverified on a device.
- Bottom bar is shown at every width (2026-11-06 night). On wide layouts (one page) the Radio/Nodes/Keypad/Log tabs scroll to the matching card and highlight; the Settings cog works everywhere, so the header has only Exit. Not yet seen on screen.
- Per-node RX highlight (2026-11-08): with several links, a node's row (green, "Hearing now") used to follow only that node's KEY/UNKEY control frames (`Iax2Session.RemoteKeyedChanged`), so a node that sent audio without them (seen on Android with 4 nodes, node 1 transmit, node 3 talking) never lit up. `MainViewModel` now also records the last `VoiceFrameReceived` time per link (`LinkWatch.LastVoiceTicks`) and the 100 ms UI tick sets `LinkItemViewModel.IsReceiving = keyed || voice within 500 ms`, and `IsRemoteKeyed` = any node receiving. Applies to the transmit node too (red border, green inside). Builds; **unverified on a device or live nodes** and no unit test (logic is in the view model). If it still fails, check the diagnostic log for voice frames from that node.

- Release status (2026-11-06 night): commits `ca4d335`, `4946c8b`, `7cdbad6` on `main`, pushed; Windows deployed via `tools/deploy-windows.ps1`. Android 1.6 (versionCode 8) signed bundle built at `keystore/nodex-1.6-code8.aab` (gitignored); upload to Play internal testing is done by the owner (release page was opened). Version 1.5 (7) is the current live internal release until then.

- **RX highlight rule, current (2026-11-08, supersedes the "keyed || voice within 500 ms" line above).** One rule in `RxActivity.IsHeard` (Core), used by the Radio screen (`MainViewModel`) and the Android Auto service:
  - A node that has **ever sent a KEY frame on this link** is "KEY-aware": it is heard only while KEY is active (KEY with no frames at all for `KeyStale` = 3 s expires, so a lost UNKEY cannot stick). Voice frames after UNKEY are ignored. Reason: East Coast HUB2 (45192) keeps sending frames while idle and flips KEY/UNKEY several times a second; the old voice rule kept it green for 45 s+ of silence (owner's screenshot + log, 2026-11-08 18:44-18:47).
  - A node that **never sends KEY** (East Coast Reflector 27339 sends only UNKEY, near-continuous voice) is heard when a voice frame arrived in the last 500 ms (`VoiceHold`).
  - Tests in `RxActivityTests`. **Confirmed by the owner on the phone with 1.12: green on East Coast Reflector works.** Known gaps: a KEY-aware node whose UNKEY is lost while it keeps streaming frames stays lit until the stream stops; a node that sends KEY late (after audio starts) lights late. A loudness threshold was considered and not needed.
- **Version display (2026-11-08).** Settings shows "Version x" under the title (`AppServices.VersionText`, the same string as the log header). Android passes `AndroidVersionName()` into `AppServices.Initialize`; Windows reads the entry assembly's informational version, which is now set by `<Version>` in `NodeX.UI.Desktop.csproj`. **Keep that `<Version>` in step with `ApplicationDisplayVersion` in `NodeX.UI.Android.csproj` at every release** (currently 1.12). Android 1.12 (code 14) shows "Version 1.0.0" in Settings (bug: it used the library version); fixed in commit 705bd99 (not yet in an Android bundle; ships with the next one, owner chose to hold until there is more to bundle). Windows deployed with 1.12 (exe ProductVersion 1.12+705bd99...).
- **Android Auto multi-link browse & disconnect (2026-11-09):** The browse tree under `nodex_root` now exposes two tabs: `Active Links` (`nodex:active`) and `Favourites` (`nodex:favorites`). `Active Links` lists each connected/reconnecting node (role Transmit/Listen-only, state, hearing status), gives a direct `Disconnect <node>` action per link (`nodex:disconnect:<node>`), and a `Disconnect all nodes` action when >1 link is up. Tapping an already connected link switches focus to it and sets it as the transmit target (`SetTransmitNode`) without disconnecting other links. New node selections from Next/favourites replace the previously active node cleanly without leaving an orphan link. Now-playing subtitle reflects multi-link count. Unit tests in `CarBrowseTests`.
- **Android Auto usability pass (2026-11-11; builds, 565 tests pass, NOT yet tried on a head unit or in a car). Supersedes parts of the 2026-11-09 entry above and the "Microphone caveat".** Owner report: screen "not really usable", wheel PTT "mostly doesn't work" and could not be unkeyed without the phone, and auto-connecting to favourites was unwanted. The causes below were found by reading the code; none is confirmed by a log yet.
  - **Car mode follows Android Auto, not a tap.** `CarConnectionWatcher` (started in `Application`) asks Android Auto whether a car is connected the way AndroidX `CarConnection` does (query `content://androidx.car.app.connection`, column `CarConnectionState`, 2 = projection; broadcast `androidx.car.app.connection.action.CAR_CONNECTION_UPDATED`; manifest `<queries>` for `com.google.android.projection.gearhead`), with no Car App Library. `CarSession.Active` = `Connected` (watcher) OR `Selected` (a node is playing on the car screen), with a `Changed` event. Before, a link started on the phone before getting in the car stayed in call-style audio (SCO / `MODE_IN_COMMUNICATION`), had no key beeps, and with `MediaKeyPtt` on a second media session competed for the wheel buttons. Now `NodeForegroundService` re-applies on `CarSession.Changed`: stops SCO, `AndroidAudioRoute.EnterCar` (mode normal, playback rebuilt as media) and `LeaveCar` afterwards; the headset-button session stays off in the car.
  - **Microphone.** The service now always asks for the `microphone` type when permission is granted (so a BLE PTT button also works in the car) and no longer calls `StartForeground` again when the types have not changed (only the notification is updated), because a repeat start from the background can lose the microphone on Android 14+. If Android refuses the type, it falls back to listen-only as before; `MainActivity.OnResume` calls `NodeForegroundService.RetryMicrophone()` so opening NodeX on the phone once gets it. A link dialled from the car screen with the phone app never opened will still be listen-only until then (platform rule).
  - **Wheel and media buttons.** While anything is keyed, Next, Back, Play, Pause and Stop only unkey (`CarTransmit.UnkeyAllAsync`); they do nothing else on that press. A refused Talk gives an error beep (`Tone.PropNack`) as well as the on-screen reason. The wheel talk button always addresses the transmit node (the now-playing screen switches to it first). A car transmission is released after `CarTransmit.MaxCarTransmit` (120 s). A link already up is adopted when the service is created, not only on the next change.
  - **Nothing is dialled unasked.** A bare Play (which Android Auto sends by itself when resuming the last media app) is ignored. Next/Back no longer dial the next favourite: they step the now-playing screen through the connected nodes only (no dial, no hang-up, no transmit change). Favourites are dialled by tapping one or by voice search.
  - **Screen.** Active Links is one row per node, "523801 · UL Cloud Node" with a status line from `CarBrowse.StatusLine` ("TRANSMIT HERE · Connected · Hearing now", "Listen only · Connected · Muted") and a coloured tile icon (`ic_car_node_tx` red, `ic_car_node_rx` slate, `ic_car_node_fav`, by `android.resource://` URI); content-style hints ask for a list. The per-node "Disconnect N" rows are gone. Tapping a connected node only shows it (it no longer moves the transmit node). The now-playing screen carries the card's buttons as custom actions: Talk / Stop talking (transmit node, `CarPtt` on) or **Transmit here** (listen-only node), Mute / Unmute, Disconnect. Exact phone-style cards are not possible: Android Auto draws every third-party screen from templates.
  - **Unverified, check on the DHU/car:** that the car-connection query answers; that the tile icons render untinted from resource URIs; how many custom actions the head unit shows before an overflow; that media-usage playback plus a `VoiceCommunication` `AudioRecord` gives usable transmit audio in the car; whether taking audio focus for an adopted phone link is acceptable (it pauses other media, and losing focus to another media app still hangs up the now-playing node).
  - **Favourites add alongside (owner decision 2026-11-11: "the app is centered around multiple connections").** Picking a favourite on the car screen connects it in addition to the links already up; the car never hangs a node up to make room (`CarBrowse.NodeToReplace` removed). Hanging up is only the Disconnect button, Pause/Stop (now-playing node) or "Disconnect all". If the favourite cannot be added (not found, link limit), the car returns to the node it was showing with an error beep and the reason. Whether a newly added node becomes the transmit node is whatever `NodeRuntime.ConnectAsync` already does; the car does not change it.
- Release status (2026-11-08 evening): Android 1.10 (12) car talk/RX/age fix, 1.11 (13) stuck-green KEY expiry, 1.12 (14) KEY-aware highlight + version line, all published to internal testing (1.12 at 7:19 PM). Next Android release will be 1.13 (code 15).

## Linux/macOS audio (added 2026-11-11)

- `NodeX.UI.Desktop/Audio/PortAudioDeviceService.cs` implements `IAudioDeviceService` with PortAudio (NuGet `PortAudioSharp2` 1.0.6, which bundles `libportaudio` for linux-x64/arm64, osx-x64/arm64, win-x64). Windows still uses NAudio; `Program.CreateAudioDevice` picks the backend.
- Streams are opened at 8 kHz mono Int16 first; if the host API refuses, they reopen at the device default rate and the class converts (area-average down for the mic via `PcmResampler`, linear interpolation up for the speaker). Playback is a 1 s ring buffer filled by `EnqueuePlaybackPcm` and drained by the PortAudio callback (silence on underrun, new samples dropped when full, same as the NAudio backend).
- **Linux dependency:** the bundled `libportaudio.so` links `libjack.so.0` and `libasound.so.2`. Verified in WSL Ubuntu: without libjack the load fails (`DllNotFoundException`). `PortAudioDeviceService.CheckAvailable()` detects this; the factory then falls back to `NullAudioDeviceService` (silent, signalling still works) and writes a "No sound: ..." line to the diagnostic log. Fix on the machine: install `libjack-jackd2-0` (or `libjack0`). PortAudio on Linux here is ALSA-only; PulseAudio/PipeWire work through ALSA's `default` device (needs the pulse ALSA plugin, `libasound2-plugins`).
- **macOS:** CoreAudio. The app needs microphone permission; a bare `dotnet run` inherits Terminal's permission, a packaged `.app` would need `NSMicrophoneUsageDescription` in Info.plist (no macOS packaging exists yet).
- **Verified:** builds; on Windows (PortAudio over the default Realtek devices) both rate paths gave about 12,200 mic samples in 1.5 s with playback buffering bounded at 50 ms (no human listening check). **Not verified:** any run on Linux or macOS, audibility, echo, latency, ARM.
- **Device pickers (added 2026-11-11, desktop only):** Settings has Microphone and Speaker ComboBoxes (hidden where `AppServices.AudioDeviceLister` is null, i.e. Android/iOS). `DesktopAudioDevices` (desktop head) lists devices (Windows: NAudio/MME names; Linux/macOS: PortAudio) and resolves a saved name to an index. The saved id (`InputDeviceId`/`OutputDeviceId`) is the device NAME, because indexes shift; an unplugged device stays selected as "<name> (not connected)" and the link falls back to the system default (logged). `Program.CreateAudioDevice` reads the settings on every link, so changes apply at the next connect. Windows MME names are truncated to 31 characters. Checked: lister and resolver output on Windows (real devices); builds. Not checked: the Settings screen visually, a saved non-default device actually carrying audio, Linux/macOS lists.

**Linux test on a Raspberry Pi (2026-11-11):** the PortAudio backend was run on the owner's Pi (Debian 13 trixie, aarch64, libjack-jackd2-0 and libasound2-plugins installed) using a self-contained `linux-arm64` publish of a small test program (not the full app). Results: `CheckAvailable()` OK; PortAudio listed a single device, `pulse`, for both input and output; capture and playback both opened on the 8 kHz path and on the forced device-rate path; 1.5 s of paced 440 Hz tone fed in with the playback buffer peaking at 40 ms and draining to 0-20 ms; the mic callback received about 12,250-12,400 samples per 1.5 s (right rate). The Pi has no microphone and only HDMI outputs, so **nobody heard the tone and no real voice was captured**. Native JACK/ALSA libraries print warnings to stderr on start-up (harmless, "jack server is not running", missing OSS/iec958 PCMs). The Avalonia desktop app itself has not been run on Linux. The Windows PortAudio-vs-NAudio and macOS paths remain separate.

**Status of this work (2026-11-11):** committed and pushed as `569e69a` ("Linux/macOS desktop audio (PortAudio) and microphone/speaker pickers") and deployed to `C:\Program Files\NodeX` on the owner's Windows PC (not launched or looked at afterwards). Full test run after the pickers: **564 passed, 0 failed** (the Android Auto entry above recorded 565; the missing test was not tracked down and no Core code was changed in this work). New code has no unit tests of its own (`PortAudioDeviceService`, `DesktopAudioDevices`, the picker properties on `SettingsViewModel`); it was exercised by hand as described above.

**Remaining for desktop audio:** (1) run the full Avalonia app on a Linux desktop (Pi with a monitor, or a PC) and on a Mac; (2) hear real audio both ways (the Pi used so far has no mic and only HDMI output); (3) macOS packaging with `NSMicrophoneUsageDescription`; (4) the Linux secret store is still a key file beside the ciphertext (§12 backlog item 12); (5) tests for the picker view-model logic and the up-sampler/ring buffer would need the playback maths split out of the PortAudio class.


## Original code-review findings (superseded; see CODE_REVIEW.md)

# NodeX Code & Specification Review Notes

**Date:** 2026-11-11
**Scope:** Complete review of `PROJECT_SPEC.md`, `README.md`, `docs/*`, `src/NodeX.Core`, `src/NodeX.UI` (Shared, Desktop, Android, iOS), `tests/NodeX.Core.Tests`, and `tools/*`.

> **Status (2026-11-11, fourth batch).** Every item in §§1–4 below has now been resolved and verified against the code and test suite (`626` passing xUnit tests across 5 consecutive runs, 0 warnings across all solution and tool targets). See the "Fourth review & backlog completion batch" entry in `PROJECT_SPEC.md` §16.
>
> - **Fixed in third & fourth batches:** all of §1, all of §2 (zero-allocation real-time audio callbacks in `PortAudioDeviceService`, `IosAudioDeviceService`, `NAudioDeviceService`, `AndroidAudioDeviceService`, `Iax2Session.SendAudioFrameAsync`, and extracted `PlaybackRingBuffer` with exact rational-phase upsampling; note that `PcmResampler` already performs full-span box-car anti-aliasing integration pinned by `InBandTone_KeepsAmplitude_OutOfBandTone_IsAttenuated`), all of §3 (except replacing Android's native `HttpMessageHandler` for `PooledConnectionLifetime`), and all of §4.

---

## 1. Bugs & Correctness Issues

- [ ] **Stop `_ageTimer` when backgrounded (`src/NodeX.UI/NodeX.UI/ViewModels/MainViewModel.cs`, lines 579–590)**
  - **Issue:** In `SetForeground(bool foreground)`, when `foreground` is `true`, both `_timer.Start()` (50 ms meter timer) and `_ageTimer?.Start()` (1 s connection-age timer) are started. When `foreground` is `false`, `_timer.Stop()` is called, but `_ageTimer?.Stop()` is omitted. On Android and iOS, `_ageTimer` continues firing every second on the UI dispatcher while the app is in the background.
  - **Fix:** Add `_ageTimer?.Stop();` inside the `else` branch of `SetForeground`.

- [ ] **Do not override loudspeaker mode with Bluetooth SCO (`src/NodeX.UI/NodeX.UI.Android/Services/NodeForegroundService.cs`, lines 229–234)**
  - **Issue:** Whenever `NodeForegroundService.Apply` runs while linked outside a car (e.g., when a second node is linked, settings are saved, or notification state refreshes), line 233 unconditionally calls `_keeper.StartBluetoothSco()`. If a Bluetooth headset is connected *and* the user had toggled the loudspeaker on (`AndroidAudioRoute.SpeakerActive == true`), `StartBluetoothSco()` switches `AudioManager.Mode` back to `Mode.InCommunication` and routes audio to the Bluetooth headset even though `SpeakerActive` remains `true`.
  - **Fix:** Guard the call with `if (!AndroidAudioRoute.SpeakerActive) _keeper.StartBluetoothSco();`.

- [ ] **Synchronize `_oSeqno` and `_iSeqno` across outbound & unsequenced frames (`src/NodeX.Core/Protocol/Iax2Session.cs`, lines 433–483, 616)**
  - **Issue:** `SendFullFrameAsync` reads `_iSeqno` and increments `_oSeqno` under `_sendLock`. However, `SendUnsequencedAsync` (used for `ACK` and `VNAK` on the receive loop thread) reads `_oSeqno` and `_iSeqno` without acquiring `_sendLock`, and `HandleFullFrameAsync` (line 616) increments `_iSeqno++` on the receive thread without synchronization against concurrent `SendFullFrameAsync` calls (such as `PING`, `LAGRQ`, `"L "` keepalive text frames, or PTT `KEY`/`UNKEY`).
  - **Fix:** Synchronize sequence number reads and increments (or route `SendUnsequencedAsync` through `_sendLock`) so `ACK` and outbound full frames never read torn or stale sequence numbers.

- [ ] **Harden bounds & label checks in raw DNS parser (`src/NodeX.Core/Services/DnsPortLookup.cs`, lines 118–175)**
  - **Issue:**
    - In `ParsePort` (lines 118–158), `rd + rdLen` is not checked against `msg.Length` before advancing `pos = rd + rdLen`, and `afterTarget` in the SRV branch (`int afterTarget = SkipName(msg, rd + 6);`) is not checked against `rd + rdLen`.
    - In `SkipName(byte[] msg, int pos)` (lines 166–175), `while (true)` reads `msg[pos]` without checking `pos < msg.Length` first, and if a malformed packet contains reserved DNS label prefix bits (`0x40` or `0x80`), `(b & 0xC0) == 0xC0` is false and `pos += 1 + b` jumps wildly instead of rejecting the packet.
  - **Fix:** Add explicit bounds checks (`if ((uint)pos >= (uint)msg.Length) return -1;`, `if (rd + rdLen > msg.Length) return 0;`) and reject non-zero upper label bits (`if ((b & 0xC0) != 0) return -1;` after checking `0xC0`).

- [ ] **Restart ID reminder clock if acknowledged while still transmitting (`src/NodeX.UI/NodeX.UI/ViewModels/MainViewModel.cs`, lines 1143–1146, 1219–1223 & `src/NodeX.Core/IdReminder.cs`)**
  - **Issue:** `MainViewModel.OnPttStateChanged` calls `_idReminder.TransmissionStarted()` only on key-down (`if (active)`). If the 10-minute ID reminder banner triggers mid-transmission and the operator identifies on the air and clicks **"ID'd — reset"** (`AcknowledgeIdReminder`) *before* unkeying, `_idReminder.Acknowledge()` stops the clock completely and no new 10-minute timer starts until the *next* key-down.
  - **Note on Spec vs. Code:** `PROJECT_SPEC.md` §17.1 (line 444) states *"the clock starts on the first key-up when none is running"*, whereas `MainViewModel` starts it on *key-down* (matching `NodeSettings.IdReminderMinutes` XML docs).
  - **Fix:** If `IsTransmitting` is true when `AcknowledgeIdReminder()` is called, immediately call `_idReminder.TransmissionStarted()`, and align the `PROJECT_SPEC.md` §17.1 description with the code.

---

## 2. Real-Time Audio & Memory Allocation Improvements

- [ ] **Eliminate heap allocations inside real-time PortAudio callbacks (`src/NodeX.UI/NodeX.UI.Desktop/Audio/PortAudioDeviceService.cs`, lines 266–315)**
  - **Issue:** PortAudio runs `OnInput` and `OnOutput` on a high-priority native real-time audio thread every 20 ms:
    - `OnInput` (lines 277, 287) allocates `new float[frameCount]` and `new short[_resampler.MaxOutputSamples(frameCount)]` every callback.
    - `OnOutput` (line 306) allocates `new short[want]` every callback.
  - **Fix:** Pre-allocate reusable instance scratch buffers on `PortAudioDeviceService` (re-allocating only if `frameCount` or `want` exceeds current buffer length) so the real-time callback performs zero managed allocations.

- [ ] **Eliminate heap allocations inside real-time iOS `AVAudioEngine` tap (`src/NodeX.UI/NodeX.UI.iOS/Audio/IosAudioDeviceService.cs`, lines 172–187)**
  - **Issue:** `OnTap` runs on a real-time CoreAudio thread and currently allocates `new float[frames]` and `new short[resampler.MaxOutputSamples(frames)]` on every tap buffer.
  - **Fix:** Pre-allocate reusable `float[]` and `short[]` scratch buffers on `IosAudioDeviceService`.

- [ ] **Reduce steady-state 50 Hz (20 ms) array allocations across the audio & IAX2 pipeline**
  - **Locations:**
    - `src/NodeX.Core/Audio/NodeAudioEngine.cs` (line 264): `byte[] ulaw = new byte[scaled.Length];` allocated every 20 ms during TX.
    - `src/NodeX.Core/Protocol/Iax2Session.cs` (line 340): `byte[] packet = new byte[Iax2Protocol.MiniHeaderSize + ulawAudio.Length];` allocated every 20 ms during TX.
    - `src/NodeX.UI/NodeX.UI.Android/Audio/AndroidAudioDeviceService.cs` (line 186): `short[] data = pcm.ToArray();` allocated every 20 ms during RX playback.
    - `src/NodeX.UI/NodeX.UI.Desktop/Audio/NAudioDeviceService.cs` (lines 117, 127): `pcm.ToArray()` on playback and `new short[samples]` on capture every 20 ms.
  - **Fix:** Reuse pre-allocated 160-sample/160-byte scratch buffers in `NodeAudioEngine` and `NAudioDeviceService`, use `ArrayPool<byte>.Shared` (or a scratch buffer) in `Iax2Session.SendAudioFrameAsync`, and use `MemoryMarshal.TryGetArray(pcm, out var segment)` in `AndroidAudioDeviceService.EnqueuePlaybackPcm` to pass `segment.Array, segment.Offset, segment.Count` directly to `AudioTrack.Write`.

- [ ] **Add anti-aliasing low-pass filter before decimation in `PcmResampler` (`src/NodeX.Core/Audio/PcmResampler.cs`, lines 43–84)**
  - **Issue:** `PcmResampler` downsamples from hardware rates (typically 48 kHz on iOS and Linux/macOS PortAudio) to 8 kHz (6:1 decimation) using 2-point linear interpolation without a low-pass filter. High-frequency energy above the 4 kHz Nyquist limit folds back into the voice band as aliasing distortion.
  - **Fix:** Apply a simple 2nd-order Butterworth IIR low-pass filter (cutoff ~3.5 kHz) to the input samples before decimation when `_inputRate > AudioFormat.SampleRate`.

---

## 3. Platform, Networking & Code Quality Polish

- [ ] **Bring iOS `Info.plist` & `AppDelegate.cs` in sync (`src/NodeX.UI/NodeX.UI.iOS/Info.plist` & `src/NodeX.UI/NodeX.UI.iOS/AppDelegate.cs`)**
  - **Version drift:** `Info.plist` (lines 9–12) has `CFBundleShortVersionString` = `1.4` and `CFBundleVersion` = `1`, whereas Desktop (`NodeX.UI.Desktop.csproj`) and Android (`NodeX.UI.Android.csproj`) are at `1.15` (build `17`).
  - **Diagnostic log version header:** `AppDelegate.ConfigureServices` (lines 36–40) does not pass `appVersion` to `AppServices.Initialize(...)`, so iOS diagnostic logs omit the release version from the header.
  - **App Store Review Guideline 2.5.4 (`UIBackgroundModes`):** `Info.plist` (line 16) declares `<string>bluetooth-central</string>` in `UIBackgroundModes`, but no `IBlePttTransport` (CoreBluetooth) is wired up on iOS yet. Either wire up an iOS `IBlePttTransport` or remove `bluetooth-central` from `UIBackgroundModes` until iOS BLE PTT is implemented so App Review does not reject the binary for declaring an unused background mode.

- [ ] **Set `PooledConnectionLifetime` on long-lived `HttpClient` instances**
  - **Locations:**
    - `src/NodeX.Core/Services/NodeResolution.cs` (line 33)
    - `src/NodeX.Core/Services/NodeDirectory.cs` (line 41)
    - `src/NodeX.Core/Services/AslRegistrationService.cs` (line 49)
  - **Issue:** Default `new HttpClient()` instances live for the entire application lifetime. Configuring `new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }` ensures DNS updates and network transitions are respected cleanly over multi-day runs.

- [ ] **Convert `async void` relay commands to `async Task` (`src/NodeX.UI/NodeX.UI/ViewModels/MainViewModel.cs`)**
  - **Locations:**
    - `DisconnectOne` (line 697)
    - `ToggleFavoriteNode` (line 926)
    - `SaveFavoriteNodeLabel` (line 940)
  - **Fix:** Change `[RelayCommand] private async void` to `[RelayCommand] private async Task` so `CommunityToolkit.Mvvm` generates `IAsyncRelayCommand` and exceptions cannot escape as unhandled `async void` faults.

- [ ] **Document or remove unused `Iax2Registrar` (`src/NodeX.Core/Services/Iax2Registrar.cs`)**
  - **Issue:** Production registration uses `AslRegistrationService` (HTTPS), while `Iax2Registrar` (UDP `REGREQ`) is unused outside unit tests and binds its own ephemeral UDP socket (`IPAddress.Any, 0`), which is separate from `Iax2Session`'s socket. Add a remark clarifying its status or remove it if superseded.

---

## 4. Specification & Documentation Updates (`PROJECT_SPEC.md` & `README.md`)

- [ ] **Update `MaxLinks` references from `4` to `8` in `PROJECT_SPEC.md`**
  - `NodeRuntime.MaxLinks` is `8` (and §2, §4.1, and §16 say `8`), but two places still say `4`:
    - `PROJECT_SPEC.md` §4.5 (line 191): `Ordered list of active links (NodeConnection, max MaxLinks = 4)` → `MaxLinks = 8`.
    - `PROJECT_SPEC.md` §4.8 (line 235): `StatusSummary ("n / 4 linked", ...)` and `"Wait until 4 are linked"` → `8`.

- [ ] **Update `EarlyHangupMaxRetries` references from `10` to `30` in `PROJECT_SPEC.md`**
  - `ReconnectPolicy.EarlyHangupMaxRetries` is `30` (~15 min at 30 s intervals), but three places still reference `10` (~6 min):
    - `PROJECT_SPEC.md` §4.6 (line 203): says `up to EarlyHangupMaxRetries (30)` and then `"(retry n of 10)"` in the same sentence.
    - `PROJECT_SPEC.md` §14 (line 395): says `(§4.6, ≈ 6 min)`.
    - `PROJECT_SPEC.md` §17 item 3 (line 446): says `(early-hangup retries are capped at 10)`.

- [ ] **Update audio-device picker status in `PROJECT_SPEC.md` and `NodeSettings.cs`**
  - Desktop audio-device pickers (`DesktopAudioDevices`) were implemented on 2026-11-11 (documented at line 615 of `PROJECT_SPEC.md`), but earlier sections still say they do not exist:
    - `PROJECT_SPEC.md` §4.7 (line 214): `InputDeviceId / OutputDeviceId (reserved, not yet used by any backend)`.
    - `PROJECT_SPEC.md` §9 (line 311): `No audio-device pickers yet (settings fields InputDeviceId/OutputDeviceId reserved)`.
    - `src/NodeX.Core/Settings/NodeSettings.cs` (lines 67–72): XML doc comments still say `"Reserved for a future device picker"`.

- [ ] **Update test count and §11 test table in `PROJECT_SPEC.md`**
  - `PROJECT_SPEC.md` line 4 and §11 (lines 328–347) still state `343 passing (xUnit)` across 14 test classes, whereas there are now **565 passing tests** across **36 test files** in `tests/NodeX.Core.Tests`.

- [ ] **Update completed housekeeping items in `PROJECT_SPEC.md`**
  - `PROJECT_SPEC.md` §3.2 (line 108) states *"there is no `global.json` — adding one to pin 10.x would be sensible"*, and §13 item 8 (line 374) states *"still to do: `.editorconfig`, README and LICENSE"*. `global.json`, `README.md`, and `LICENSE` now exist at the repository root (only `.editorconfig` remains optional).

- [ ] **Fix duplicate section numbering and path typo in `PROJECT_SPEC.md`**
  - Both `## 17. Amateur-radio compliance` (line 440) and `## 17. Troubleshooting log` (line 577) are numbered `17`, with Appendices A and B placed between §16 and §17.
  - Line 425 has missing backslashes in `C:Program FilesNodeX` → `C:\Program Files\NodeX`.

- [ ] **Update `README.md` "Known limitations" (lines 69–70)**
  - *"At least one public hub drops links after about 45 seconds for reasons not yet understood."* — Resolved by sending periodic `"L "` link-list text frames (`Iax2Session.LinkListInterval`, documented in `PROJECT_SPEC.md` line 429).
  - *"Reconnecting from a different public IP is not yet verified."* — Field-verified working on 2026-11-11 across Wi-Fi ↔ cellular handoff (`PROJECT_SPEC.md` line 52).
