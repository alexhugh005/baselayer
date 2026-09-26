# Base style application — visual review

The frontend now follows `base-style-guide/STYLE-GUIDE.md`.

## Changes

- White left sidebar on desktop, compact header on mobile, original Base logo reference, warm Conduit page canvas.
- Grounded green usage/onboarding panels, Livewire primary actions, neutral raised cards.
- Larger heading and data hierarchy; shared radii, spacing, focus, hover, disabled, error, and selected states.
- Consistent device rows, status badges, recommendations, history, connection forms, permission/meter controls, and confirmation dialogs.
- Clerk appearance variables aligned to the same palette.
- All three summary metrics remain visible on mobile; settings and dialogs scroll without horizontal overflow.
- Skip link and reduced-motion styles.

Business logic and APIs are unchanged. PP Neue Montreal is the preferred family; until a licensed font is supplied, the app uses its local sans-serif fallback. The previous remote Google Fonts dependency was removed.

## Verification

Production build and 19 existing tests passed. Desktop and effective 390px mobile views were inspected in Chrome. The live home changed connection state during inspection; recommendation/approval/empty screenshots use an isolated, non-operative data fixture (labeled Visual QA fixture), not live telemetry. The temporary fixture was removed after verification.

- `dashboard-desktop.png`, `dashboard-mobile.png`: fixture usage, recommendation and devices.
- `connect-desktop.png`: live connection form, opened without submitting.
- `settings-desktop.png`: live settings, viewed without saving.
- `settings-mobile.png`: fixture permission/meter layout; no horizontal overflow.
- `shutoff-review-desktop.png`, `shutoff-review-mobile.png`: fixture approval review; no command sent.
- `onboarding-desktop.png`: empty-home fixture.

Clerk colors are configured through its typed appearance API. The active session was preserved, so the signed-out third-party flow was not completed during this review. Backend/device control behavior was not changed or exercised by this restyle.

## Sidebar follow-up

Restored the persistent left navigation with the original white surface, warm neutral active state, green logo/text, and bottom account area. Desktop capture: `sidebar-desktop.png`. Production build passed; effective 390px mobile check showed no horizontal overflow.
