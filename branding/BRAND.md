# NodeX brand

**Name:** NodeX  **Tagline:** AllStarLink Radioless Node

## Logo
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
| X fill | `#3F6FE0` |
| X outline on dark | `#B8CCFF` |
| X outline on light | `#10126E` |
| Circuit lines/nodes (brand accent) | `#F59E0B` (matches the app's `AmberAccentBrush`) |
| Wordmark on dark / secondary text | `#F0F6FC` / `#8B949E` |

Functional UI colours are unchanged: green = connect/RX, red = TX, cyan = links. Amber is for the logo only.

## Files (`exports/`)
App icon master 1024, Play Store icon 512, Play feature graphic 1024x500, horizontal lockups (dark/light), mark PNGs.
In the app: Android adaptive + themed launcher icons, notification icon, splash; Windows `.ico` (window + exe); browser favicon; header logo.

## Not done
iOS app icon (`NodeX.UI.iOS` has no asset catalogue; use `exports/nodex-app-icon-1024.png` when one is added), Play Store listing screenshots.
