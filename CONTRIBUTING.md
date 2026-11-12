# Contributing to NodeX

Thanks for your interest. NodeX is a small project, so please open an issue to discuss anything big before writing code.

## Before you start

- Read [`PROJECT_SPEC.md`](PROJECT_SPEC.md) §0, §3 (build) and §12 (conventions and known pitfalls).
- **The code and its tests are the source of truth.** Almost every rule in the spec is pinned by a test. If you change behaviour, change the test and the spec together.
- Keep NodeX logic in `NodeX.Core` where practical; only platform bindings go in the heads.

## Build and test

```bash
dotnet build src/NodeX.UI/NodeX.UI.Desktop
dotnet test  tests/NodeX.Core.Tests --blame-hang-timeout 60s
dotnet test  tests/NodeX.UI.Tests
```

CI runs the same on Windows. Please make sure it passes.

UI tests use a separate Avalonia Headless/Skia xUnit v3 project; Core tests remain xUnit v2. Keep those dependencies separate. Set `NODEX_SCREENSHOT_DIR` to an external directory to export the UI rendering checks. See `THIRD_PARTY_NOTICES.md` before reusing/distributing the desktop BLE dependency commercially.

## Safety rules for transmit code

NodeX can key real radio transmitters. Changes touching push-to-talk, car or Bluetooth control must:

- be off by default behind an explicit setting if they add a new way to transmit;
- honour listen-only, the transmit-node rule and the time-out timer;
- never key on connect, reconnect or device attach/detach;
- never be tested against a node you do not control without its owner's knowledge.

## Pull requests

- Keep them focused, with tests for new Core logic.
- Say what you tested and on which platform. Be explicit about anything you could not verify.
- Do not commit keystores, passwords, node credentials or other secrets.

## Bugs

Use the issue template. The in-app **Send logs** feature produces a log with passwords already removed; please check it before posting.
