# NodeX code and specification review status

Updated 2026-11-11. This describes current source, not the status of a deployed phone build. The original review findings and old line numbers are preserved in `HISTORY.md`; they are not an unchecked to-do list.

## Resolved and verified in automated tests

- Background UI meter/age timers stop and restart together.
- Android foreground routing preserves a chosen loudspeaker, avoids SCO in car mode and gives headset media keys to one session at a time. Idle registration holds no audio locks or microphone type. These decisions are used directly by the service through `AndroidServicePlan`.
- Unchanged foreground types refresh the notification without another microphone-service start. Car media callbacks use tested `CarMediaButtons`: every button unkeys first, bare Play does not dial, and Talk needs an explicit wheel mapping.
- IAX sequence/send ordering, DNS bounds, ID reminder acknowledgment, call cancellation, connected-only PTT, authentication and retransmission fixes remain covered by Core tests.
- ASL link/status text parsing, playback ring-buffer arithmetic, device-picker settings, BLE learning and desktop BLE transport lifecycle are covered.
- A separate `NodeX.UI.Tests` project hosts real MainViewModel behavior on Avalonia's dispatcher: first run, timers, disconnect confirmation, transmit-target loss, listen-only/microphone gates and Spacebar release when an overlay opens. It also renders themes/enlarged text with Skia.
- Appearance validation, persistence and settings transfer cover Dark/Light/HighContrast and 100–150% text sizes.

## Implemented; still needs native-device checks

Desktop BLE uses Windows/Linux/Apple Silicon bindings and the shared Learn wizard. Native scanning/pairing/notifications have not been tried with a real button. Linux/macOS keyring integration, desktop audio/pickers, the macOS bundle, mobile notification delivery and permission explanations also need platform checks. Tests of shared Android service decisions do not emulate Android's OS lifecycle, permissions or audio focus.

The owner reports the Desktop Head Unit working great, Android screen-off audio lasting an hour or more, working Wi-Fi/cellular handoff and good transmitted voice quality. Keep those reports distinct from instrumented traces and individual feature checks.

## Deliberately deferred or corrected findings

- The three reported `async void` methods were callbacks, not relay commands; each catches errors already. Do not rename them based on the obsolete finding.
- `PcmResampler` already performs full-span box-car anti-alias integration; it was not the reported two-point interpolator.
- Audio callbacks reuse scratch buffers and IAX mini-frame sends use pooling. This is not a claim that the entire audio pipeline allocates nothing: `NodeAudioEngine` still owns a separate encoded frame for each queued TX packet. Further allocation tuning is optional and must preserve queue ownership.
- `PooledConnectionLifetime` has not been set. Replacing the default Android native HTTP handler was deliberately deferred, not resolved by code.
- Normal links use an ephemeral local source port; nonstandard remote destination ports are supported independently. A fixed local bind is not required for the owner's successful outbound connections.

## Validation commands

```powershell
dotnet test tests/NodeX.Core.Tests --blame-hang-timeout 60s
dotnet test tests/NodeX.UI.Tests
dotnet build NodeX.sln
dotnet build src/NodeX.UI/NodeX.UI.Android -p:PlayStore=true
```

Current suite: 664 Core/service cases and 18 headless UI cases. Hosted CI results must be checked separately from local results.
