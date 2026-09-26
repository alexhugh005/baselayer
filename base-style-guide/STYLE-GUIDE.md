# Base Power — website style guide

Research snapshot: September 25, 2026, America/Chicago (September 26 UTC). Source: [Base Power](https://www.basepowercompany.com/), with additional inspection of [/core](https://www.basepowercompany.com/core), [/energy](https://www.basepowercompany.com/energy), and [/about](https://www.basepowercompany.com/about).

This is a working reference for future BaseHack design work, assembled from the public website. It is not an official brand manual. **Measured** means read from production CSS or computed styles; **observed** means visible in a captured page or verified interaction; **recipe** means our recommended way to recreate the behavior. Brand assets remain Base Power's; downloaded files are reference material, not a license grant. Font files are identified but not bundled.

## 1. Visual character

Base pairs practical energy hardware with a warm domestic setting. The main palette is off-white, deep green, and light lime. The layout is spacious and direct: clear sans-serif headings, few decorative borders, rounded photographs, and restrained shadows. Family scenes, homes, plants, technicians, and real installations make the technical product feel familiar.

There are two complementary modes:

- **Service/product UI:** precise grids, utility selectors, plan cards, numbered steps, readable sans-serif text, short action labels.
- **Editorial storytelling:** masking-tape labels, handwritten script, orange underline strokes, textured paper, an angled founder letter, immersive photography and scroll reveals. Use these touches selectively; they are accents rather than the default treatment for every card.

Start with [the desktop hero](screenshots/01-home-desktop.png), [energy split hero](screenshots/17-energy.png), and [founder letter](screenshots/14-about.png).

## 2. Color system — measured

These names occur in the source stylesheet, rather than being invented descriptions.

| Brand name | Hex | Typical use |
| --- | --- | --- |
| Grounded | `#1E4D2B` | Deep green brand text, active steps, promotional panels, footer |
| Livewire | `#B2DD79` | Primary buttons, highlighted icons |
| Conduit | `#F0EEEB` | Warm off-white page background |
| Terminal | `#292826` | Default dark text |
| Strike | `#FFFFFF` | Cards, forms, inverse text |
| Energy | `#ED6C30` | Orange feature progress and editorial accents |
| Goldenrod | `#F7C33C` | Rating stars and secondary accent |
| Texas Sky | `#048EE5` | Secondary accent / illustration palette |

Supporting values: subtle green `#D6F0B4`; green hover token `#77A45A`; active dark green `#102A17`; border `#D8D7D5`; secondary gray `#54524F`; muted gray `#7F7D7A`; disabled gray `#A9A8A7`; error red `#C51808`; pale error `#FFCCC7`; grid-off indicator `#BF5249`.

**Implementation nuance:** the shared stylesheet contains both `.bpc-*` component styles and utility classes. The current homepage's primary buttons specify a pale-green hover (`brand-primary-subtle`), whereas `.bpc-button-filled:hover` uses the darker green-hover token. Match the actual component being reproduced instead of assuming the existence of a token proves its use everywhere.

Use [tokens.css](tokens.css) for our prefixed reusable aliases and recipes. [tokens.json](tokens.json) is a compact machine-readable palette. Raw values and exact class names are in [home-computed-styles.json](evidence/home-computed-styles.json).

## 3. Typography — measured

The principal typeface is **PP Neue Montreal**, with 400 regular, 500 medium, 600 semibold, and 700 bold declarations; italic variants also appear in the CSS. **Clarendon Wide Bold** is declared as the display family and **Dahlia Blues** as the script family. A declared family is not proof that every page or heading uses it: the homepage headings we measured use Neue Montreal. The handwritten tape label illustrates the script role.

| Role | Desktop measured size / line height | Weight / detail |
| --- | --- | --- |
| Homepage H1 | 48 / 52.8px | 600, normal tracking |
| Main section H2 | 40 / 44px | 600; green or Terminal depending on section |
| Product card title | 32 / 38.4px | 600 |
| Small heading | 20 / 27px | 600 |
| Active Core rail title | 20 / 28px | 600, .2px tracking |
| Inactive Core rail title | 16 / 22.4px | 600, muted |
| Main body / standard CTA | 16 / 24px | Body 400–500, CTA 600; many utilities use .2px tracking |
| Small CTA / text link | 14 / 21px | 600 |
| Small body / caption | 12px or 11px source tokens | 1.5 line-height |

At a **390 × 844 CSS-pixel viewport**, the homepage H1 measures **40 / 44px**, not the smaller legacy `.bpc-heading-*` mobile token. The production stylesheet also declares fluid display sizes (`clamp(3rem, 5.6vw, 5.5rem)` and `clamp(3rem, 4.7vw, 4.25rem)`) for larger editorial contexts. Do not apply these to the homepage H1 indiscriminately.

Use system sans as a development fallback; typography will differ until licensed typefaces are supplied. The local reference gallery intentionally uses system fonts rather than silently fetching font binaries.

## 4. Layout, spacing, shape

**Measured:** the spacing scale is based on 4px, with 8, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, and 128px steps. Controls commonly use 8px corner radii; media 16px; installation steps 20px; large panels 24px. A pill radius is reserved for switches and pill-like UI, not every primary button.

The Core rail has a maximum width of 1300px and a desktop grid ratio of 520:676 (copy:image), with a fluid 44–104px gap. Its screen padding is `clamp(40px, 9svh, 88px)` vertically and `clamp(28px, 4.86vw, 70px)` horizontally. Other sections should be measured independently; do not treat that one component's dimensions as a universal site container.

**Observed:** use generous spacing between story sections, restrained spacing within related text, and large images. Product cards are often open layouts directly on the canvas, rather than white boxed cards. Group with alignment and whitespace before adding extra borders.

The CSS includes 150ms control transitions, 300ms navigation color transitions, `cubic-bezier(.3,.3,.3,1)` soft easing, and 2px focus outlines with 2px offset. The rail uses a different easing curve, `cubic-bezier(.22,1,.36,1)`.

## 5. Component and feature recipes

### Navigation / mega menu

[Desktop expanded example](screenshots/02-products-mega-menu.png) · [Mobile menu](screenshots/16-mobile-menu.png)

**Observed:** a fixed header overlays the homepage image with a white logo and text. Below the hero it becomes a white header with dark text. The Products menu expands into a wide white panel with two information columns, subtle vertical rules, thumbnail links, and a deep-green availability card. The active category has a pale rounded background and up-chevron. On narrow screens, the full navigation is replaced by a logo and menu button; the opened menu shows stacked expandable categories and wide action buttons.

**Recipe:** one shared header, theme driven by hero intersection and menu-open state. Use disclosure buttons with `aria-expanded`, accessible link groups, Escape dismissal, and controlled focus. Collapse desktop navigation at the content-fit breakpoint. Avoid implementing the menu as an inaccessible hover-only list; click expansion was verified. A pointer left over the trigger can reopen the menu during inspection.

### Homepage hero / ZIP-code entry

[Desktop](screenshots/01-home-desktop.png) · [Mobile](screenshots/15-home-mobile.png)

**Observed:** full-bleed home photography, dark overlay, star rating, one prominent headline, short supporting copy, a white ZIP input with a green location icon, lime CTA, and small trust text beneath. Desktop text sits near the lower-left of the image. On mobile it moves to the upper center, headline wraps to two lines, and input/button stack at almost full width. The home remains visible below the copy.

**Recipe:** image with `object-fit:cover` and responsive focal position, overlay layer, then real HTML copy and form. Use a constrained content width and change alignment at the breakpoint. Keep the mobile photograph composition intentional rather than merely squeezing a desktop crop. Use a real input label even if the visible design relies on a placeholder. No ZIP was submitted during research; validation, results, and error states remain unverified.

### Primary, outline, and text actions

**Measured:** standard utility button is 16px/24px semibold, 12px vertical and 16px horizontal padding, 8px radius, 8px icon gap. Small header button is 14px/21px, 8px × 12px padding, with 44px minimum height. Primary fill is Livewire with Grounded text. Secondary actions are transparent with a 1px green or white border, chosen for their background. Text links are green 14px semibold plus a small chevron.

**Recipe:** use a small variant/size API and semantic anchor vs button behavior. Preserve visible focus and disabled styling. The CSS aliases in this folder contain a starter recipe; they are not exported Base source components.

### Grid on/off explainer

[Grid on](screenshots/04-grid-on.png) · [Grid off](screenshots/03-grid-off.png)

**Observed and exercised:** switching changes the pill label, colored toggle, house illustration, panel background, heading, and description. The off state dims the surrounding environment while the house windows and battery remain illuminated. The switch exposes an accessible name and checked state.

**Assets:** `assets/grid-on.webp` and `assets/grid-off.webp` are original site illustrations. This observed component uses authored raster states; recreating a live 3D scene is unnecessary for the same visual result.

**Recipe:** one boolean state drives every visual/text change together. Preload both illustrations and crossfade, preserve aspect ratio, and expose a native/ARIA switch. Do not communicate grid status with color alone.

### Core scroll feature rail

[Reference](screenshots/05-core-feature-rail.png) · [Exact source CSS](evidence/CoreRailSection.C164Ltc4.css)

**Measured implementation:** a tall outer section becomes armed with data attributes and contains a `position:sticky` viewport-height screen. Four rows sit beside an image stack. The orange rail fills through `scaleY`; CSS variables control step opening and image opacity/scale. Inactive descriptions collapse using `grid-template-rows:0fr`, active descriptions expand to `1fr`; the active title grows and becomes green. Images are absolutely layered with `object-fit:cover`. A hand-drawn underline reveals with `clip-path`.

The armed desktop section height is `n × 115svh`; mobile uses `n × 130svh`. At 768px the layout becomes a two-column grid. Below 768px the image moves above the copy and is `clamp(150px,24svh,220px)` high. Below 721px desktop viewport height or below 601px mobile height, CSS disables the sticky treatment and shows all descriptions in a static layout. This short-screen fallback is an especially useful detail to preserve.

**Recipe:** normalized section scroll progress drives the active row and image; use requestAnimationFrame/IntersectionObserver to limit work, then update CSS variables. This describes a reconstruction strategy, not a claim about their JavaScript internals. Add a reduced-motion static mode and ensure all content remains readable without animation. The saved screenshot records a moment in the crossfade, so slight image overlap is intentional motion evidence.

### Product cards

[Reference](screenshots/06-product-cards.png)

**Observed:** three equal desktop columns, rounded lifestyle images, small lime-backed category icons, large titles, short explanations, green chevron links. These sit on the warm canvas without prominent card borders or shadows. Image, title, body, and action alignment establish the group.

**Recipe:** responsive grid, consistent media ratio, `object-fit:cover`, flexible text row and consistent action placement. Use one column on narrow screens as a sensible reconstruction; the mobile card section was not separately captured.

### Utility selector / revealed pricing

[Selector open](screenshots/07-pricing-selector.png) · [After Oncor selection](screenshots/08-pricing-revealed.png)

**Observed and exercised:** the unselected pricing area is blurred behind an in-section green card. A white combobox opens a rounded white listbox with grouped utility options, scrollable choices, and a pinned fallback option. Selecting ATX/DFW (Oncor) reveals a white plan card beside benefit copy. Rate, installation cost, and recurring membership fee have separate hierarchy; fine-print detail is smaller.

**Recipe:** explicit unselected, open, selected, and unsupported states. Keep the overlay local to the pricing section instead of dimming the entire document. Use accessible combobox/listbox behavior and store a selected utility only as needed. The prices in screenshots are time-bound source examples, not reusable constants or current offers.

### Installation steps with linked image

[Second step active](screenshots/09-installation-steps.png)

**Observed and exercised:** three stacked numbered panels sit beside a photograph. The selected step becomes deep green with white text and an expanded description; inactive steps are white and shorter. Selecting the second step changes the image to an installation van/crew scene. Measured panel padding is 24px and radius 20px; color transition is 200ms.

**Recipe:** selected step index drives panel expansion, theme, and companion image. Use disclosure buttons, preserve heading semantics, preload images, and stack the media with steps on small screens.

### FAQ

[Expanded example](screenshots/10-faq-expanded.png)

**Observed and exercised:** broad text rows on the canvas, thin dividing rules, a trailing plus/minus affordance, and answers revealed beneath the selected question. Desktop row padding measures 32px vertically; utility classes reduce it to 24px then 20px on smaller layouts.

**Recipe:** disclosure buttons or `details/summary`, explicit expanded state, normal document flow for the answer, and no fixed-height clipping. Keep answer line lengths comfortable. The opened example is a business-model question; copy is reference content, not part of the reusable component contract.

### Social proof / footer

The homepage uses a monochrome media-logo strip, rating stars, customer photographs, quotes, and repeated review cards. Previous/Next controls are 44px squares with 8px corners, a white background, and a subtle border. The full carousel interaction was not exercised, so autoplay behavior is not asserted.

[Footer capture](screenshots/11-footer.png) records the lower dark-green legal section. The site also exposes grouped footer navigation, contact details, app links, and social links above it. Use subdued inverse text and compact typography for dense legal content, with clearer hierarchy for navigation. The screenshot shows the lower footer, not the entire navigation area.

### Energy and editorial variants

[Energy page](screenshots/17-energy.png) uses a side-by-side hero: dark text on Conduit, a ZIP entry, small benefit icons, and a rounded domestic photo with overlapping bill-comparison graphics. [About](screenshots/14-about.png) presents the founder message as an angled sheet with tape, paper texture, shadow, and signature. [Core intro](screenshots/12-core-product-page.png) is an immersive product-image opening; [scroll reveal](screenshots/13-core-editorial.png) records text partway through its reveal. These are intentional state captures, not evidence of missing permanent copy.

## 6. Logos, imagery, and iconography

The original Base logo is an inline SVG with viewBox `0 0 337 126`: a compact, slanted uppercase wordmark enclosed in a rounded rectangular frame. Use its paths; do not approximate it with a font. Original extraction and three explicitly colored derivatives are in [assets](assets/). Derived variants retain the geometry but remove page-specific classes and resolve `currentColor` for use as standalone images.

**Recommended handling, not official logo policy:** retain the roughly 2.67:1 ratio, use a single high-contrast color, leave breathing room, and never stretch or redraw it. No official minimum-size or clear-space standard was found in the reviewed pages.

Photography emphasizes warm natural light, recognizable homes, family activity, and the battery installed in context. Technical illustrations use an approachable miniature/isometric house, muted materials, and luminous energy paths. Decorative texture is tactile and restrained: torn tape, paper grain, orange strokes. Interface icons typically use simple 16–24px outlines; category icons use green-backed motifs. Avoid replacing this with an unrelated glossy icon library.

## 7. Applying this guide later

1. Start with Conduit background, Terminal body copy, Neue Montreal (or the stated fallback), and Grounded brand text.
2. Choose an observed layout archetype: photo hero, split hero, open product grid, interactive feature pair, or editorial story.
3. Use Livewire for the principal action and outline/text links for secondary actions.
4. Match spacing, radii, and text hierarchy before adding decoration.
5. Keep imagery domestic and product-specific; reserve tape/script for editorial accents.
6. Include mobile composition, keyboard operation, empty/error states, and reduced-motion handling as implementation requirements. Some of these are recommendations beyond the inspected states.
7. Consult the relevant screenshot and measured evidence; do not infer the entire system from one page or silently substitute the larger display type scale.

## 8. Scope and provenance

Main desktop browser viewport: 1512 × 861 CSS pixels; captured raster dimensions can differ slightly due to browser screenshot behavior. Mobile override: 390 × 844. Browser viewport was reset after inspection. Screenshots are actual browser captures, not recreated mockups. A narrow floating reading toolbar in some images belongs to a browser extension and is **not Base UI**; related extension CSS variables were removed from the curated evidence.

Reviewed visually: homepage, Core introduction, energy hero, About letter. Exercised: desktop menu, mobile menu, grid switch both directions, utility selection, installation step selection, FAQ expansion. Not inspected: signed-in dashboard, completed signup, payment, form validation, all breakpoints, every carousel state, or every feature's JavaScript. No purchase, account creation, or personal-data submission occurred.

Source CSS snapshots and computed-style measurements are preserved under `evidence/`; asset URLs and extraction methods are in `assets/manifest.json`. Public content and assets may change; this is a dated design reference. Keep original third-party assets separate from new product assets and check rights before shipping them publicly.
