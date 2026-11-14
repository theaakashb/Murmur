# Settings and onboarding visual direction

## Purpose

Bring Murmur's settings and onboarding into the visual world of the redesigned [Murmur website](https://github.com/theaakashb/Murmur-Site/tree/38fd675c993e724c2782c23cf44ff6ee0d04fc6b), while preserving the native Mac experience and existing app behavior. Settings and onboarding implement this direction with scoped appearance colors, blue primary actions, modest serif headings and a static native paper-and-voice drawing. Shared notification colors and behavior remain outside this scope.

## Visual vocabulary

- Quiet paper-like surfaces, clear dark ink, aligned text, strong information hierarchy and restrained illustration.
- Reference colors: paper `#F4F4EF`, ink `#20221F`, accent blue `#244BDF`, sage `#DCE5DC`, supporting blue `#E5E9FF`. Adapt these into semantic, accessible light/dark variants rather than applying light colors unchanged in dark mode.
- The website uses DM Sans. Native semantic text styles, legibility, keyboard navigation and macOS control conventions take precedence in the app.
- Illustration depicts a blue voice ribbon becoming writing on paper. Use it sparingly in onboarding or empty states, away from dense working controls. The website's former human hero figure was rejected and must not be reused.
- Settings should offer clear grouping, comfortable density, readable labels and consistent spacing. Preserve established navigation and discoverability unless a visual improvement needs a small, behavior-preserving adjustment.

## Scope and protected behavior

The permitted changes are accent colors, settings appearance and onboarding visual presentation. Notification and alert behavior must remain unchanged, including triggers, timing, dismissal, actions, wording/semantics, focus handling, sounds and presentation lifecycle. Do not modify notification/alert handlers or route behavior as part of this visual redesign.

Preserve dictation, recording, transcription, cleanup, insertion, shortcuts, provider selection, permissions, privacy disclosures and persistence behavior. Onboarding styling must not change permission requests, model download logic or first-run sequencing. Prefer styling scoped to settings/onboarding when a global token would affect protected surfaces.

## Acceptance criteria

- Settings and onboarding share a coherent visual identity with the website while retaining native controls and usable density.
- Text, icons, selection, focus, disabled states and interactive controls remain legible in light and dark appearances. Respect reduced motion and keyboard operation.
- Notifications and alerts retain their existing behavior; review the diff and relevant checks against the baseline.
- Capture native screenshots with synthetic data, including settings and onboarding in supported light/dark appearances. Include useful before/after comparisons and images embedded in the implementation PR.
- Run the relevant checks documented in CONTRIBUTING.md, build the native app, and inspect the rendered UI. Distinguish synthetic fixture results from live recording or insertion evidence.

## References

- [Website design system](https://github.com/theaakashb/Murmur-Site/blob/38fd675c993e724c2782c23cf44ff6ee0d04fc6b/DESIGN.md)
- [Desktop website preview](https://github.com/theaakashb/Murmur-Site/blob/38fd675c993e724c2782c23cf44ff6ee0d04fc6b/research/murmur-redesign/built-desktop.png)
- [Current paper-and-voice hero](https://github.com/theaakashb/Murmur-Site/blob/38fd675c993e724c2782c23cf44ff6ee0d04fc6b/public/hero-thought.webp)

These references describe a visual direction, not a request to transplant the website's large headlines or page layouts into app windows.
