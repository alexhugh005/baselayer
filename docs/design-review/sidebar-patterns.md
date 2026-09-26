# Sidebar refinement

Reviewed September 25, 2026.

## References

- [Calendly screens on Mobbin](https://mobbin.com/apps/calendly-web-15cbcaca-a6c4-421c-a732-dc7a7842ad42/4487cba0-bdea-5402-b662-531afc830e12/screens): consistent navigation icon alignment; secondary utilities grouped at the foot of the rail. Its collapse control is at the top; our lower placement follows the requested interaction.
- [Flux collapsible sidebars](https://fluxui.dev/blog/2025-09-03-collapsible-sidebars): persistent icon rail, labeled controls, retained navigation, remembered preference.

## Applied

Grouped the account and collapse action into a bottom footer with a single separator. Replaced the detached right-aligned icon and negative margin with a full-width, left-aligned utility row. The 18px chevron aligns with navigation icons, with a quiet 13px label and transparent resting background. Collapsed mode centers the icon in a 44px-high target. Existing Base colors, white sidebar, focus treatment, accessible name, and stored preference remain.

Verified expanded and collapsed layouts in the browser, preference after reload, and the mobile header. Production build passed.
