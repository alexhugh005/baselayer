# Sources and capture notes

Captured September 25, 2026 America/Chicago / September 26 UTC. All page and asset sources are public Base Power resources. Homepage inspection supplied the main production stylesheet and measured styles. No external design roundup was used as evidence.

## Primary pages

- [Homepage](https://www.basepowercompany.com/)
- [Base Core](https://www.basepowercompany.com/core)
- [Energy](https://www.basepowercompany.com/energy)
- [About](https://www.basepowercompany.com/about)

## Preserved stylesheets

- [MarketingLayout.Bi5_DK1u.css](https://www.basepowercompany.com/_bpc/assets/MarketingLayout.Bi5_DK1u.css) — palette, typography, shared and utility rules.
- [CoreRailSection.C164Ltc4.css](https://www.basepowercompany.com/_bpc/assets/CoreRailSection.C164Ltc4.css) — sticky feature section, progress, crossfade and responsive fallbacks.

Their original files are preserved under `evidence/`. They are evidence, not stylesheets to import wholesale into the app. `tokens.css` is our small curated derivative with suggested component recipes. The computed-style export includes some offscreen menu elements with layout boxes; it is not a claim that every exported element was simultaneously visible.

## Captures

Desktop inspection used a 1512 × 861 CSS-pixel viewport; mobile override used 390 × 844. PNG raster dimensions can differ slightly. All captures are viewport screenshots, not full-page composites.

| File | Source page | State / purpose |
| --- | --- | --- |
| [01-home-desktop.png](screenshots/01-home-desktop.png) | [/](https://www.basepowercompany.com/) | Homepage hero: Full-bleed domestic photography, white overlay type, lime conversion action, compact trust cues. |
| [02-products-mega-menu.png](screenshots/02-products-mega-menu.png) | [/](https://www.basepowercompany.com/) | Desktop navigation: White mega menu, thin column dividers, descriptive links and a dark-green availability panel. |
| [03-grid-off.png](screenshots/03-grid-off.png) | [/](https://www.basepowercompany.com/) | Grid off: The illustration, panel tone, label, heading and description switch as a single state. |
| [04-grid-on.png](screenshots/04-grid-on.png) | [/](https://www.basepowercompany.com/) | Grid on: Compare with the off state: brighter environment and green status indicator. |
| [05-core-feature-rail.png](screenshots/05-core-feature-rail.png) | [/](https://www.basepowercompany.com/) | Core feature rail: Sticky two-column story with image crossfades, active feature copy and an orange progress line. Captured during transition. |
| [06-product-cards.png](screenshots/06-product-cards.png) | [/](https://www.basepowercompany.com/) | Product choices: Open three-column cards on the warm canvas. Rounded media, category glyph, heading, short body and text CTA. |
| [07-pricing-selector.png](screenshots/07-pricing-selector.png) | [/](https://www.basepowercompany.com/) | Utility selector: In-section blurred pricing preview with an inset green prompt and grouped white listbox. |
| [08-pricing-revealed.png](screenshots/08-pricing-revealed.png) | [/](https://www.basepowercompany.com/) | Pricing after selection: Plan card distinguishes rate, installation and recurring fee; benefit copy sits beside it. Historic pricing example. |
| [09-installation-steps.png](screenshots/09-installation-steps.png) | [/](https://www.basepowercompany.com/) | Installation steps: The selected numbered panel expands, turns green and changes the linked image. |
| [10-faq-expanded.png](screenshots/10-faq-expanded.png) | [/](https://www.basepowercompany.com/) | FAQ disclosure: Thin rules, broad text rows and a trailing expand/collapse control. One answer shown open. |
| [11-footer.png](screenshots/11-footer.png) | [/](https://www.basepowercompany.com/) | Lower footer: Deep green background and compact inverse legal copy. This capture is the lower footer region. |
| [12-core-product-page.png](screenshots/12-core-product-page.png) | [/core](https://www.basepowercompany.com/core) | Core image introduction: Immersive product-in-context imagery; captured at the opening media state. |
| [13-core-editorial.png](screenshots/13-core-editorial.png) | [/core](https://www.basepowercompany.com/core) | Core scroll reveal: Textured warm surface, oversized sans-serif words and orange underline. Captured partway through reveal. |
| [14-about.png](screenshots/14-about.png) | [/about](https://www.basepowercompany.com/about) | Founder letter: Angled paper, tape, texture and a signature introduce a personal editorial mode. |
| [15-home-mobile.png](screenshots/15-home-mobile.png) | [/](https://www.basepowercompany.com/) | Mobile hero: Centered headline and stacked form controls; the image preserves room for the house below. |
| [16-mobile-menu.png](screenshots/16-mobile-menu.png) | [/](https://www.basepowercompany.com/) | Mobile navigation: Stacked categories, full-width actions, compact logo and close button. |
| [17-energy.png](screenshots/17-energy.png) | [/energy](https://www.basepowercompany.com/energy) | Energy split hero: Text/form on the left, a rounded domestic image and bill-comparison graphics on the right. |

## Asset handling

`assets/manifest.json` records source URLs and logo extraction. The original logo uses `currentColor`; named ink, green, and white files are locally normalized variants, with original path geometry intact. The original logo has a 337 × 126 viewBox. Font names and URLs are recorded in the evidence inventory but font binaries are not downloaded.

The curated asset inventory excludes analytics URLs, tracking images, and unrelated scripts. The computed variable export excludes Speechify extension variables. A floating reading toolbar can still appear in some screenshots; it is browser-extension chrome and should not be copied into future components.

## Limits

This is a dated public-site design inspection. Form submission, account dashboard, payments, validation, all breakpoints, carousel autoplay, and complete animation timing were not tested. The Core intro and editorial images intentionally show intermediate visual states. Prices and claims are historical source content; do not copy them as current commercial facts. Original photos, logos, and site CSS retain their owners' rights.
