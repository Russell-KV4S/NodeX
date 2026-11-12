<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="branding/exports/nodex-lockup-dark.png">
    <img alt="NodeX" src="branding/exports/nodex-lockup-light.png" width="420">
  </picture>
</p>

<p align="center"><b>An AllStarLink node for your phone or computer. No Asterisk, no radio, no hardware.</b></p>

<p align="center">
  <a href="https://github.com/Russell-KV4S/NodeX/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Russell-KV4S/NodeX/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
</p>

NodeX is a standalone [AllStarLink](https://www.allstarlink.org/) (ASL) node written in C# (.NET 10, Avalonia UI). It is a real peer node on the ASL network, speaking IAX2 over UDP and registering over HTTPS. It is **not** a WebTransceiver or a guest softphone. Audio comes from your device's microphone and speaker, or from a Bluetooth headset or speaker-mic whose button acts as push-to-talk.

It is built for a **portable node**: register once, link out to other nodes, and stay connected while the screen is off and while you move between Wi-Fi and cellular.

## Features

- Registers with AllStarLink and links **outbound** to other nodes (up to 8 at once; one transmit node, the rest listen-only, with per-node mute)
- Find-a-node search, favourites, and DTMF function-code macros
- Push-to-talk: on-screen (momentary or toggle), Bluetooth LE button with a "Learn" wizard, headset media key
- Auto-reconnect and network handoff
- Listen-only mode (no callsign needed, PTT disabled)
- Optional station-ID reminder (it never transmits for you)
- Android Auto: listen-only media app; the full GitHub edition also offers steering-wheel/Talk PTT (off by default)
- Full Android edition: GitHub update checks and a verified download/install flow (public feed access pending)
- Passwords kept in the platform secret store; no telemetry
- Dark, Light and High contrast themes, with text size from 100% to 150%
- Permission explanations in Settings, with Android permission review and a Not now option

## Platforms and status

NodeX is young. This table says what has actually been tested.

| Platform | Status |
|---|---|
| **Windows** | Works. Core protocol validated live against a real ASL3 node. |
| **Android** | On Google Play internal testing; used on a real phone. Background behaviour, Bluetooth PTT and network handoff are still being tested on devices. |
| **Android Auto** | Earlier DHU use succeeded. The latest Play report found missing PTT (disabled in that edition) and an icon regression, fixed in 1.16 but awaiting a car check. Individual voice/focus/cold-start cases still need recording. |
| **Linux / macOS** | Audio backend written (PortAudio); tested only with a small test program on a Raspberry Pi. The full app has not been run there. |
| **iOS** | Compiles. Never run on a device; no release planned. See [`docs/IOS_RELEASE.md`](docs/IOS_RELEASE.md) if you want to pick it up. |

Details of what is and is not verified are in [`docs/LIVE_VALIDATION.md`](docs/LIVE_VALIDATION.md) and §2 and §14 of [`PROJECT_SPEC.md`](PROJECT_SPEC.md).

## Amateur radio and licensing

Many ASL nodes key real radio transmitters. **Transmitting through NodeX is amateur-radio transmission. You need a valid amateur licence to transmit, and you are responsible for identifying yourself as your licence requires.** NodeX does not verify callsigns and never sends an automatic ID or any audio of its own; the ID reminder only prompts you. If you only want to listen, use Listen-only mode.

Respect node owners' linking policies. Don't test by keying a node you don't control without its owner's knowledge.

## Getting started

You need an AllStarLink account and a registered node number and password (from the [ASL portal](https://www.allstarlink.org/)). Enter them on the first-run screen.

**Downloads:** [GitHub Releases](https://github.com/Russell-KV4S/NodeX/releases) provide the full Android APK and self-contained Windows, Linux and macOS packages for x64 and ARM64. iOS is excluded. Releases are currently private, so repository access is required. See each release's installation notes and [`docs/RELEASING.md`](docs/RELEASING.md).

For Android Auto PTT, use `NodeX-android.apk`, then enable Talk from the car and choose a wheel button in Settings. The Play edition omits car PTT. Switching from Play may require uninstalling because the signing keys differ; export settings first and be prepared to re-enter your node password. Future same-key sideload updates preserve app data. The full edition has Settings → App updates; in-app delivery remains unavailable while releases are private, so download through GitHub manually for now.

**Build from source** (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download); see `global.json`):

```bash
dotnet build src/NodeX.UI/NodeX.UI.Desktop      # core, UI and desktop app
dotnet test  tests/NodeX.Core.Tests             # unit tests
dotnet test  tests/NodeX.UI.Tests               # headless UI and rendering tests
dotnet run   --project src/NodeX.UI/NodeX.UI.Desktop
```

The Android head additionally needs the .NET `android` workload (`dotnet build src/NodeX.UI/NodeX.UI.Android`). Android signing keys are excluded from the repo; release automation uses encrypted Actions secrets. Shared desktop/Android version numbers live in `Directory.Build.props`.

Build output is redirected to `%LOCALAPPDATA%\NodeX\build` by `Directory.Build.props` (set `NODEX_BUILD_ROOT` to change it). On Linux you also need `libjack-jackd2-0` and ALSA libraries for sound; without them NodeX runs silently.

## Known limitations

- Outbound links only. Cellular carriers block inbound IAX2, so NodeX ignores inbound links.
- A hub can refuse your link for some minutes after you register or change network, until it has learned your node's address. NodeX keeps retrying (up to about 28 minutes).
- Moving between Wi-Fi and cellular drops and re-dials the link (an IAX2 call cannot change address). It works in day-to-day use, with a delay while the new address registers; the delay has not been measured.
- IPv4 only.
- Linux and macOS use the OS keyring (`secret-tool` / `libsecret` on Linux, Keychain on macOS) when available, falling back to a `0600` key file beside the ciphertext on minimal/headless systems without a keyring daemon.
- Desktop BLE uses the existing Scan / Learn setup on Windows and glibc Linux (x64/ARM64), and macOS 15+ on Apple Silicon. It needs an enabled adapter; Linux also needs BlueZ/D-Bus. Real-button validation is still needed. Intel Macs and older macOS are unsupported by the bundled BLE library.

Outbound calls use an available local UDP source port. This does not restrict the remote node's destination port; nonstandard remote ports are looked up independently.

## Documentation

- [`PROJECT_SPEC.md`](PROJECT_SPEC.md): architecture, protocol facts, behaviour reference, backlog
- [`docs/LIVE_VALIDATION.md`](docs/LIVE_VALIDATION.md): results from tests against real nodes
- [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`SECURITY.md`](SECURITY.md)

## Credits

Built with [Avalonia](https://avaloniaui.net/), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [NAudio](https://github.com/naudio/NAudio) and [PortAudioSharp2](https://github.com/k2-fsa/portaudiosharp2). Protocol details come from RFC 5456 and the AllStarLink `app_rpt` source. NodeX is an independent project and is not affiliated with or endorsed by AllStarLink.

## License

[MIT](LICENSE) © Russell Thomas (KV4S)

NodeX source is MIT licensed. Desktop BLE includes SimpleSharpBLE/SimpleBLE under a separate noncommercial-use license; see [third-party notices](THIRD_PARTY_NOTICES.md).
