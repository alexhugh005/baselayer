# Base Power style guide

Start with **[index.html](index.html)** for the local visual reference, or **[STYLE-GUIDE.md](STYLE-GUIDE.md)** for the detailed implementation guide.

Captured September 25, 2026 (America/Chicago). Reviewed the public Base Power homepage, Core, Energy, and About pages. This is an independent reference, not an official brand manual.

## Contents

- `index.html` — offline visual guide and screenshot atlas; no build step or external dependencies.
- `STYLE-GUIDE.md` — colors, type, layout, component anatomy, responsive rules, motion and implementation recipes.
- `tokens.css` / `tokens.json` — reusable aliases and source-backed palette.
- `assets/` — original inline SVG logo, three normalized color variants, hero photo, tape texture, and grid on/off artwork; provenance in `manifest.json`.
- `screenshots/` — 17 actual desktop/mobile browser captures.
- `evidence/` — public CSS snapshots, computed measurements, filtered asset inventory.
- `SOURCES.md` — source URLs, capture context, and limitations.

Open `index.html` directly in a browser. For a local HTTP preview, run `python3 -m http.server 8765 --bind 127.0.0.1 --directory base-style-guide` from the repository root, then open http://127.0.0.1:8765.

For future implementation, read the complete guide first: it distinguishes measured production values, observed behavior, and suggested reconstruction. Fonts are identified but not bundled. Third-party graphics are reference material; ownership and reuse rights remain with their respective holders.
