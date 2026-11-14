# Settings and onboarding visual direction

## Purpose

Bring Murmur's settings and onboarding into a neutral white/black and royal-blue identity inspired by the redesigned website. The current PR screenshots with green/sage surfaces and pale-blue accents are rejected. The requirements below supersede the previous sage/paper palette; implementation of this correction is pending. Preserve native Mac conventions and all existing behavior.

## Settings layout

Settings uses a dedicated seven-destination sidebar: General, Dictation, Writing,
Meetings, Privacy & Storage, Providers, and Updates. Back to Murmur returns to
Home. Search retains direct links to individual controls.

Dictation groups Recording, Transcription, and Insertion into tabs. Writing groups
Cleanup, Modes, and Dictionary. General contains visibility, startup, and appearance;
permissions live under Privacy & Storage. Existing preferences and processing routes
are preserved.

The approved Linear-inspired direction uses system typography, restrained heading
weights, neutral layered surfaces, subtle separators, compact controls, and royal-blue
selection indicators. Settings styling is scoped independently of Home, onboarding,
and status notices. Both light and dark appearances remain supported. Optional
explanations use accessible information buttons; privacy and insertion consequences
remain visible.

## Visual vocabulary

- The only app theme colors are neutral white, black/grayscale, and royal blue. No sage, green, olive, teal, cream or green-tinted secondary text. Neutral grays have equal RGB channels.
- Light mode: white or neutral near-white sidebar and content, black/neutral dark text. Dark mode: black or neutral charcoal sidebar and content, white/neutral light text. Example dark surfaces: `#111111`, `#181818`, `#242424`; do not use the rejected green-blacks `#1D211F` or `#28352D`.
- Sole brand accent: **`#244BDF` (RGB 36, 75, 223), in both appearances**. Replace the legacy baby blue `#5995C9` and its accent derivatives. Do not substitute pale `#A8BCFF` in dark mode. Trace both `Palette` and `EditorialPalette` consumers so legacy accents do not leak into settings, navigation or onboarding.
- Use royal blue for primary actions, selected indicators, underlines, icons, links and progress as appropriate. White primary-button labels must remain readable. If small royal-blue text fails contrast on a dark surface, use a neutral label plus a royal-blue indicator/fill; do not solve it by changing the brand to baby blue. Verify focus and disabled states too.
- Keep broad surfaces neutral. Subtle blue selection treatments must not become a second pastel-blue brand or a large tinted canvas. The website's occasional sage illustration highlight is not authorized for the app theme.
- Illustration, if retained, uses neutral ink/paper and a royal-blue voice ribbon only. No green fill, rejected human hero or unrelated decorative colors.
- Native semantic text styles, legibility, keyboard navigation, accessible contrast and usable settings density take precedence over marketing typography. Keep the correction focused; use the Settings organization above while preserving the onboarding layout.

## Scope and protected behavior

The permitted changes are accent colors, settings appearance and onboarding visual presentation. Notification and alert behavior must remain unchanged, including triggers, timing, dismissal, actions, wording/semantics, focus handling, sounds and presentation lifecycle. Do not modify notification/alert handlers or route behavior as part of this visual redesign.

Preserve dictation, recording, transcription, cleanup, insertion, shortcuts, provider selection, permissions, privacy disclosures and persistence behavior. Onboarding styling must not change permission requests, model download logic or first-run sequencing. A shared accent color may change for visual consistency, including on protected surfaces, but this does not authorize notification/alert behavior changes. Review shared-token consumers before editing; preserve handlers and lifecycle logic.

## Acceptance criteria

- All app-owned theme surfaces are neutral white/black/grayscale with royal-blue `#244BDF` accents; no green/sage tint or legacy baby-blue accent remains. Settings and onboarding retain native controls and usable density.
- Text, icons, selection, focus, disabled states and interactive controls remain legible in light and dark appearances. Respect reduced motion and keyboard operation.
- Notifications and alerts retain their existing behavior; review the diff and relevant checks against the baseline.
- Replace the rejected after screenshots with corrected native settings/onboarding screenshots in light/dark and compact layouts using synthetic data. Embed images in the implementation PR and label historical images clearly.
- Run the relevant checks documented in CONTRIBUTING.md, build the native app, and inspect the rendered UI. Distinguish synthetic fixture results from live recording or insertion evidence.

## References

- [Website design system](https://github.com/theaakashb/Murmur-Site/blob/38fd675c993e724c2782c23cf44ff6ee0d04fc6b/DESIGN.md)
- [Desktop website preview](https://github.com/theaakashb/Murmur-Site/blob/38fd675c993e724c2782c23cf44ff6ee0d04fc6b/research/murmur-redesign/built-desktop.png)
- [Current paper-and-voice hero](https://github.com/theaakashb/Murmur-Site/blob/38fd675c993e724c2782c23cf44ff6ee0d04fc6b/public/hero-thought.webp)

These references describe a visual direction, not a request to transplant the website's large headlines or page layouts into app windows.
