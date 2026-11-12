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
