# NodeX brand

**Name:** NodeX  **Tagline:** AllStarLink Radioless Node

## Logo
**Current (owner-supplied, 2026-11-06):** navy X with amber outline and amber circuit traces, `screen.png` (raster, transparent) -> `logo/nodex-logo-light.png` (as uploaded) and `logo/nodex-logo-dark.png` (navy fill lifted to #3F6FE0 for dark UI). `build_assets.py` uses these for the "dark"/"light" marks. App icon = light logo on a pale blue #E8EEFC tile. The SVG masters below are the earlier AI blue-X mark and are now used only for the mono icons.

An X with four circuit traces per side ending in nodes. Masters are in `source/` (SVG, transparent):
- `nodex-mark-dark.svg`: for dark backgrounds (app UI, app icon, store art)
- `nodex-mark-light.svg`: for white/light backgrounds
- `nodex-mark-mono.svg`: single-colour outline (Android themed icon, notification icon)

Regenerate every PNG/ICO and push them into the app projects: `python build_assets.py` (needs `pip install pymupdf pillow`).
`original/` holds the first AI-generated logo; `explorations/` holds rejected colour options.

## Colours
| Use | Hex |
|---|---|
| Brand background (= app console bg) | `#0D1117` |
| X fill (dark version) | `#3F6FE0` |
| X outline on dark | `#B8CCFF` |
| X fill (light version, as uploaded) | `#10126E` |
| App icon tile | `#E8EEFC` |
| Circuit lines/nodes (brand accent) | `#F59E0B` (matches the app's `AmberAccentBrush`) |
| Wordmark on dark / secondary text | `#F0F6FC` / `#8B949E` |

Functional UI colours are unchanged: green = connect/RX, red = TX, cyan = links. Amber is for the logo only.

## Files (`exports/`)
App icon master 1024, Play Store icon 512, Play feature graphic 1024x500, horizontal lockups (dark/light), mark PNGs.
In the app: Android adaptive + themed launcher icons, notification icon, splash; Windows `.ico` (window + exe); browser favicon; header logo.

## Not done
The iOS icon asset catalogue, Play icon and feature graphic exist. Demo phone-layout screenshots are in `docs/screenshots/`. Remaining: real-phone/car screenshots and verification of the iOS asset catalogue on a Mac.
