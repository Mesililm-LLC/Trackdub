# Design Standards

This document collects the visual design tokens and patterns used by Trackdub. It combines evidence from the marketing site (`trackdub.com`) and the Avalonia/CLI UI code in this repository.

## 1. Design Principles

Trackdub's product voice is built around a small set of principles. These appear repeatedly in the marketing copy and should guide UI decisions:

- **Local by default**: media, transcripts, and generated audio stay on the user's disk.
- **Deterministic runs**: the same manifest plus the same models produces the same output.
- **Cross-platform**: Windows, macOS, and Linux share the same project format.
- **Open manifest**: every bundled model is itemized with source, checksum, and license.
- **No account required**: the tool does not depend on cloud identity.
- **Per-line regen**: a single line, speaker, or stage can be regenerated without redoing everything.
- **Resumable jobs**: checkpoints let the app close and continue from the last completed stage.
- **CPU fallback, always**: every stage can run without an accelerator.
- **Your disk, your files**: the user owns and controls project output.

Source: `trackdub.com/` landing page, value-pillar copy, 2026-07-26.

## 2. Brand & Voice

- **Tagline**: "Dub videos into other languages without giving up control."
- **Meta description**: "Trackdub is a local-first desktop workstation for dubbing video into other languages. Editable stages, deterministic runs, your media stays on your machine."
- **Open Graph image**: `https://trackdub.com/og.png`, 1200 x 630.
- **Site structure** (navigation): Pipeline, Try it, Resume, Control, Performance, Local-first, Manifest, Pricing, FAQ, Launch list.
- **Page narrative**: "A desktop workstation", "Built for control at every stage", "Edit a line. Watch what invalidates.", "Pause anything. Edit one stage. Resume only what changed.", "Runs on the hardware you already have.", "Your media is yours. Here is the whole map."

Source: `trackdub.com/src/routes/index.tsx` and `__root.tsx` head meta, 2026-07-26.

## 3. Color

### Marketing site palette

The site uses **Tailwind CSS v4** with `@theme inline` and CSS custom properties. The default light theme is described in `styles.css` as the warm-cream "Autumn Harvest" persona. A `.dark` class provides a dark blue theme. Individual page sections also override these with "chapter" utilities.

#### Brand anchors

| Token | oklch | Approximate hex | Role |
|---|---|---|---|
| `--paper` | `oklch(0.955 0.024 78)` | `#f5ede0` | warm cream background |
| `--paper-2` | `oklch(0.915 0.032 74)` | | deeper cream |
| `--ink-raw` | `oklch(0.14 0.028 45)` | | charred brown-black |
| `--ash` | `oklch(0.215 0.028 46)` | | warm graphite |
| `--burgundy` | `oklch(0.32 0.115 32)` | | deep wine |
| `--rust` | `oklch(0.44 0.14 42)` | | mid rust |
| `--amber` | `oklch(0.74 0.17 62)` | | warm amber accent |
| `--gold` | `oklch(0.86 0.15 82)` | | soft gold accent |
| `--cream` | `oklch(0.965 0.024 78)` | | text on dark chapters |

#### Default (paper) semantic tokens

| Token | Value | Role |
|---|---|---|
| `--background` | `var(--paper)` | page background |
| `--foreground` / `--ink` | `var(--ink-raw)` | body text |
| `--surface` | `var(--paper-2)` | elevated surface |
| `--surface-2` | `var(--ash)` | dark surface / accent block |
| `--card` | `color-mix(in oklch, var(--paper) 92%, var(--ink-raw) 8%)` | card background |
| `--card-foreground` | `var(--ink-raw)` | card text |
| `--popover` | `var(--paper)` | popover background |
| `--primary` | `var(--ink-raw)` | primary button / link |
| `--primary-foreground` | `var(--paper)` | text on primary |
| `--secondary` | `var(--paper-2)` | secondary background |
| `--muted` | `var(--paper-2)` | muted background |
| `--muted-foreground` | `color-mix(in oklch, var(--ink-raw) 72%, var(--paper) 28%)` | secondary text |
| `--accent` | `var(--amber)` | accent color |
| `--accent-foreground` | `var(--ink-raw)` | text on accent |
| `--destructive` | `oklch(0.55 0.2 27)` | error / destructive |
| `--border` / `--hairline` | `color-mix(in oklch, var(--ink-raw) 28%, var(--paper) 72%)` | borders |
| `--input` | `color-mix(in oklch, var(--ink-raw) 22%, var(--paper) 78%)` | input borders |
| `--ring` | `var(--amber)` | focus rings |
| `--radius` | `0.25rem` (4 px) | corner radius |

#### Dark semantic tokens

The `.dark` class uses a slate-blue base (`oklch(0.129 0.042 264.695)` background, `oklch(0.984 0.003 247.858)` foreground) with the same Tailwind semantic map (`--background`, `--foreground`, `--primary`, `--secondary`, `--muted`, `--accent`, `--destructive`, `--border`, `--input`, `--ring`, etc.).

#### Chapter themes

`styles.css` defines six chapter utilities that override semantic tokens per section. They are applied to section IDs on the home page.

| Utility | Background | Surface 2 | Foreground | Accent | Sections |
|---|---|---|---|---|---|
| `chapter-paper` | `var(--paper)` | `var(--ash)` | `var(--ink-raw)` | `var(--amber)` | `#top`, `#pipeline`, `#architecture` |
| `chapter-cream` | paper + gold mix | paper + gold mix | `var(--ink-raw)` | `var(--burgundy)` | `#control`, `#pricing` |
| `chapter-ash` | `var(--ash)` | `var(--ink-raw)` | `var(--cream)` | `var(--gold)` | (fallback for alternate sections) |
| `chapter-ink` | `var(--ink-raw)` | ink + gold mix | `var(--cream)` | `var(--gold)` | `#walkthrough`, `#performance`, `#faq` |
| `chapter-burgundy` | `var(--burgundy)` | `var(--ink-raw)` | `var(--cream)` | `var(--gold)` | `#resume`, `#privacy` |
| `chapter-rust` | ash + rust mix | `var(--ink-raw)` | `var(--cream)` | `var(--gold)` | `#requirements` |

Selection is tinted with the current accent: `background: color-mix(in oklch, var(--accent) 40%, transparent)`.

#### Waveform accents

SVG waveform graphics on the landing page use these `oklch()` values (they predate or coexist with the CSS chapter system):

| Token | oklch | Suggested role |
|---|---|---|
| `--wave-muted` | `oklch(0.48 0.02 245)` | quiet / background waveform |
| `--wave-base` | `oklch(0.55 0.03 240)` | dark waveform base |
| `--accent-blue` | `oklch(0.72 0.15 258)` | primary accent |
| `--accent-amber` | `oklch(0.72 0.15 50)` | secondary / warning accent |
| `--accent-gold` | `oklch(0.78 0.17 62)` | highlight / active accent |

Source: `trackdub.com/src/styles.css`, `trackdub.com/src/routes/index.tsx`, 2026-07-26.

### Application palette (Avalonia)

The `DubBench` Avalonia app ships two theme dictionaries. These are the canonical application color tokens.

Dark theme (`src/DubBench/Resources/DubBenchTheme.axaml`):

| Brush | Hex | Usage |
|---|---|---|
| `ThemeBackgroundBrush` | `#1A1A1A` | window / app background |
| `ThemeSurfaceBrush` | `#2D2D2D` | elevated surfaces |
| `ThemeAccentBrush` | `#00BFA5` | primary accent |
| `ThemeTextPrimaryBrush` | `#E8E8E8` | body text |
| `ThemeTextSecondaryBrush` | `#9E9E9E` | muted text |
| `ThemeBorderBrush` / `BorderBrush` | `#3D3D3D` | borders |
| `ThemeErrorBrush` | `#FF5252` | errors |
| `TabActiveBrush` | `#00BFA5` | active tab |
| `TabInactiveBrush` | `#6B6B6B` | inactive tab |
| `ThemeForegroundBrush` | `#E8E8E8` | foreground |
| `SidebarBackgroundBrush` | `#1E1E1E` | sidebar background |
| `ContentBackgroundBrush` | `#252525` | content area |
| `ButtonPrimaryBackground` | `#00BFA5` | primary button |
| `ButtonPrimaryText` | `#1A1A1A` | primary button text |
| `SuccessGreenBrush` | `#4CAF50` | success |
| `WarningOrangeBrush` | `#FF9800` | warning |

Light theme (`src/DubBench/Resources/DubBenchThemeLight.axaml`) maps the same keys to lighter equivalents; the accent becomes `#009688` and backgrounds flip to off-white / white.

Source: `src/DubBench/Resources/DubBenchTheme.axaml`, `src/DubBench/Resources/DubBenchThemeLight.axaml`.

### Hardcoded UI accents

A few views use colors that are not yet in the theme dictionaries:

| Color | Location | Usage |
|---|---|---|
| `#3A5535` | `LeaderboardTabView.axaml` | disclaimer banner background |
| `#2D5A27` | `LeaderboardTabView.axaml` | score badge background |
| `#CCFFCC` | `LeaderboardTabView.axaml` | score badge foreground |

These should be migrated to theme brushes if they become part of the shipping UI.

Source: `src/DubBench/Views/LeaderboardTabView.axaml`.

### Terminal UI colors

The CLI TUI uses Spectre.Console's default named colors:

- `Color.Blue`: main header border (`Trackdub`)
- `Color.Grey`: footer and placeholder panels
- `Color.Cyan1`: inline picker border

These are not currently tied to the Avalonia theme.

Source: `src/Trackdub.Cli/Tui/TrackdubTuiApp.cs`, `src/Trackdub.Cli/Tui/TuiInlinePicker.cs`.

### Known palette tension

The application accent is teal (`#00BFA5`), while the website accent is chapter-dependent (`var(--amber)` or `var(--gold)` in most chapters, with `var(--burgundy)` buttons on paper/cream chapters). If a single brand palette is required, reconcile the marketing site `oklch()` accents with the Avalonia `ThemeAccentBrush`.

## 4. Typography

### Marketing site

The site self-hosts three font families in `public/fonts/`:

| Family | Weights | Usage |
|---|---|---|
| **IBM Plex Sans** | 400, 500, 600, 700 (plus 400 italic) | body, headings, UI labels |
| **Instrument Serif** | 400 (plus 400 italic) | display headings, logo wordmark, numerals |
| **JetBrains Mono** | 400, 500 | code, timecode, captions, data |

The two display weights are preloaded in `__root.tsx`:

- `/fonts/instrument-serif-400.woff2`
- `/fonts/ibm-plex-sans-400.woff2`

CSS custom properties in `styles.css`:

```css
--font-sans: "IBM Plex Sans", ui-sans-serif, system-ui, -apple-system, sans-serif;
--font-serif: "Instrument Serif", "IBM Plex Serif", Georgia, ui-serif, serif;
--font-mono: "JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, monospace;
```

`body` sets:

- `font-family: var(--font-sans)`
- `font-feature-settings: "ss01"`
- `-webkit-font-smoothing: antialiased`
- `text-rendering: optimizeLegibility`

Custom `@utility` helpers:

- `.font-serif`: family, `font-weight: 400`, `letter-spacing: -0.01em`, `font-feature-settings: normal`
- `.font-mono`: family only

#### Type scale (from `index.tsx`)

| Element | Classes | Notes |
|---|---|---|
| H1 (hero) | `font-serif text-5xl leading-[0.98] tracking-tight sm:text-6xl lg:text-[68px] xl:text-[76px]` | max-width container |
| H2 (section) | `font-serif text-4xl leading-[1.05] tracking-tight sm:text-5xl` | section headings |
| H3 | `font-serif text-3xl leading-[1.1] tracking-tight sm:text-4xl` | stage sub-headings |
| Logo wordmark | `font-serif text-2xl leading-none tracking-tight sm:text-[38px]` | masthead |
| Section number | `font-mono text-2xl leading-none tracking-tight text-accent sm:text-3xl` | chapter index |
| Section label | `font-serif text-[20px] leading-tight tracking-tight sm:text-[24px]` | chapter title |
| Body | `text-[17px] leading-relaxed` or `text-lg leading-relaxed` | paragraphs |
| Small body | `text-[16px] leading-relaxed`, `text-[15px]`, `text-[14px]` | cards, captions |
| Micro label | `font-mono text-[10px] uppercase tracking-[0.18em]` / `[0.22em]` / `[0.14em]` | chapter tags, metadata |
| Mono data | `font-mono text-[11px]`, `text-[12px]`, `text-[13px]` | job logs, UI lists |
| Price | `font-serif text-5xl tracking-tight` | pricing cards |

`tabular-nums` is used for timecode and numeric data.

Source: `trackdub.com/src/styles.css`, `trackdub.com/src/routes/index.tsx`, `trackdub.com/src/routes/__root.tsx`, `trackdub.com/public/fonts/`, 2026-07-26.

### Application

The Avalonia theme does not set a custom `FontFamily`; it relies on the system font. The theme defines the following type scale in `DubBenchTheme.axaml` and `DubBenchThemeLight.axaml`:

| Token | Size | Typical use |
|---|---|---|
| `FontSizeMicro` | 10 | micro labels |
| `CaptionFontSize` | 12 | captions |
| `MonospaceFontSize` | 13 | monospace / data |
| `BodyFontSize` | 14 | body text |
| `HeadingFontSize` | 18 | section headings |
| `FontSizeTitle` | 20 | page title |

In views, headings use `FontSize="18"` and `FontWeight="SemiBold"`.

Source: `src/DubBench/Resources/DubBenchTheme.axaml`, `src/DubBench/Views/*.axaml`.

## 5. Spacing & Sizing

### Marketing site

| Token / Pattern | Value | Usage |
|---|---|---|
| Section max-width | `max-w-[1600px]` | hero, full-bleed sections |
| Content max-width | `max-w-6xl` | `Container` component |
| Page padding | `px-6 sm:px-10` | horizontal page gutters |
| Section padding | `py-12 sm:py-9` (hero), `py-20 sm:py-28` (content) | vertical rhythm |
| Masthead height | `h-16 sm:h-[88px]` | site header |
| Masthead gap | `gap-6` | nav / actions spacing |
| Primary nav gap | `gap-x-7` | desktop nav links |
| Hero grid | `lg:grid-cols-[minmax(0,0.78fr)_minmax(0,1.22fr)]` | text + waveform |
| Content grid | `lg:grid-cols-12` with `lg:gap-16` | two-column sections |
| Content gap | `gap-10`, `gap-12`, `gap-14` | between text and media |
| Card padding | `p-8` | pricing cards |
| Button padding | `px-6 py-3` | primary CTA |
| Corner radius | `--radius: 0.25rem` (4 px) | cards, buttons, inputs |
| Focus ring | `ring-2 ring-accent ring-offset-2 ring-offset-background` | focus-visible state |

### Application

Common values from the Avalonia views:

| Token | Value | Usage |
|---|---|---|
| Small padding | 6, 8, 10, 12 | cards, list items, buttons |
| Section margin | `0,0,0,16` | bottom margin on scroll panels |
| Separator margin | `0,4` | horizontal separators |
| Corner radius | 4, 6 | cards, badges, banners |
| Border thickness | 1 | cards, sidebars |
| Button heights | 28, 32, 36 | secondary / primary actions |
| Button widths | 100, 120, 160, 200 | fixed-width action buttons |
| Sidebar width | 50 px | `BenchmarkWindow` left rail |
| ContentControl margin | 16 | main content area |
| Window size | 960 x 720 | `BenchmarkWindow` default |
| Window min size | 640 x 480 | `BenchmarkWindow` minimum |

Source: `trackdub.com/src/routes/index.tsx`, `trackdub.com/src/styles.css`, `src/DubBench/Views/BenchmarkWindow.axaml`, `src/DubBench/Views/*.axaml`.

## 6. Layout

### Application shell

- A 50 px left sidebar holds tab buttons (emoji icons, 36 x 36).
- The main content area is a `ContentControl` with 16 px margin.
- Each tab is a `ScrollViewer` wrapping a vertical `StackPanel` with `Spacing="12"`.
- Common vertical rhythm: heading (18, SemiBold), description text (Opacity 0.7), separator, input groups (`Spacing="4"`), action button, result card.

Source: `src/DubBench/Views/BenchmarkWindow.axaml`, `src/DubBench/Views/AudioPrepTabView.axaml`, etc.

### Marketing site layout

The landing page is a long-scrolling page with numbered chapters:

1. Hero + tagline
2. Pipeline (six editable stages)
3. Try it / interactive transcript demo
4. Resume / checkpoint explanation
5. Control / invalidation
6. Performance / hardware lanes
7. Local-first / data ownership
8. Manifest
9. Pricing (Personal / Pro / Teams)
10. FAQ
11. Waitlist / launch list

**Global components:**

- `Container`: `mx-auto w-full max-w-6xl px-6 sm:px-10`
- `SectionNumber`: chapter index, label, and underline tick
- `Rule`: `h-px w-full bg-border` horizontal separator
- `Masthead`: `border-b border-border bg-background`, `max-w-[1600px]`, `h-16 sm:h-[88px]`
- `SectionRail`: kinetic scroll progress rail on the left side (desktop)
- `Colophon`: 4-column footer with product, developers, company, and status links

**Grid conventions:**

- Hero: two-column `minmax` text / waveform grid.
- Content sections: `grid-cols-1 lg:grid-cols-12` with text in `lg:col-span-4` and content/media in `lg:col-span-8`.
- Pricing: `md:grid-cols-3` with `divide-x divide-border`.
- FAQ: `md:grid-cols-[220px_1fr]` for question / answer pairs.

Source: `trackdub.com/src/routes/index.tsx`, 2026-07-26.

## 7. Components

### Marketing site components

**`InkButton`**

- Base: `btn-sheen inline-flex items-center gap-2 px-6 py-3 text-[14px] font-medium` with sheen sweep on hover/focus.
- Primary: `bg-[var(--burgundy)] text-[var(--cream)] border-[var(--burgundy)] hover:bg-[var(--rust)]`.
- Ghost: `text-[var(--burgundy)] border-[var(--rust)] hover:bg-[var(--rust)] hover:text-[var(--cream)]`.

**`TextLink`**

- `inline-flex items-baseline gap-1 border-b border-foreground/30 pb-0.5 text-foreground hover:border-accent hover:text-accent`

**`MotionToggle`**

- Stores motion preference in `localStorage` under `trackdub:motion`.
- Respects `prefers-reduced-motion`.
- Adds `html.reduce-motion` class, which disables animations site-wide.

**`WaitlistForm`**

- Email input with client-side Zod validation.
- Cloudflare Turnstile rendered explicitly to avoid SSR race conditions.
- Posts to `/api/waitlist`, with cross-origin fallback to `https://trackdub.com` for `trackdub.dev`.

**Utility classes:**

- `.btn-sheen`: hover lift `translateY(-1px)`, sheen sweep animation `900ms cubic-bezier(0.22, 1, 0.36, 1)`.
- `.card-lift`: hover `translateY(-2px)`, top `border-top-color` transitions to `--accent`.
- `.shadow-panel`: `0 1px 0 oklch(1 0 0 / 0.03) inset, 0 24px 60px -30px oklch(0 0 0 / 0.45)`.
- `.hairline`: `border-color: var(--hairline)`.

### Avalonia components

#### Buttons

- Primary action: fixed width/height, `ButtonPrimaryBackground` / `ButtonPrimaryText`.
- Icon tab buttons: transparent background, `Foreground` bound to `TabActiveBrush` / `TabInactiveBrush`.

#### Cards

- Border: `BorderBrush`, thickness 1, `CornerRadius="6"`.
- Padding: 12.
- Background defaults to `ThemeSurfaceBrush` / `ContentBackgroundBrush`.
- Result/status cards use `Spacing="4"` for stacked labels.

#### Lists

- List items: `CornerRadius="4"`, `Padding="10"`, bottom `Margin="0,0,0,4"`.
- Background: `SidebarBackgroundBrush`.

#### Badges

- Score badge: background `#2D5A27`, foreground `#CCFFCC`, `CornerRadius="4"`, `Padding="8,4"`.

#### Banners

- Disclaimer banner: background `#3A5535`, `CornerRadius="6"`, `Padding="12"`.

#### Separators

- `Separator` with `Background="{DynamicResource BorderBrush}"` and `Margin="0,4"`.

#### Form groups

- `StackPanel Spacing="4"` with a `TextBlock FontWeight="SemiBold"` label and an input control (`TextBox`, `ComboBox`, `NumericUpDown`).

#### CLI panels

- `Panel` with `BorderColor` from Spectre.Console (`Blue`, `Grey`, `Cyan1`), 1 padding unit.

Source: `trackdub.com/src/styles.css`, `trackdub.com/src/routes/index.tsx`, `src/DubBench/Views/*.axaml`, `src/Trackdub.Cli/Tui/*.cs`.

## 8. Icons

- The Avalonia app uses emoji strings stored in `BenchmarkIcons.axaml`:
  - `OnnxModelIcon`: 🔬
  - `AudioPrepIcon`: 🎤
  - `DubbingIcon`: 🎬
  - `PresetsIcon`: ⚙️
  - `LeaderboardIcon`: 🏆
- The marketing site uses **Lucide** icons (`lucide-react`) and inline SVG waveforms.

Source: `src/DubBench/Resources/BenchmarkIcons.axaml`, `trackdub.com/src/routes/index.tsx`, `trackdub.com/package.json`.

## 9. Animation & Motion

### Marketing site

`styles.css` defines a focused, reduced-motion-aware kinetic layer.

| Animation | Duration / Easing | Usage |
|---|---|---|
| `fade-up` | `0.6s ease-out` | generic fade-up entrance |
| `reveal` | `320ms cubic-bezier(0.22, 1, 0.36, 1)` | section entrance |
| `reveal-child` | `300ms` same easing, delay `calc(40ms + var(--reveal-i, 0) * 30ms)` | staggered children |
| `wave-drift` | `3.6s ease-in-out infinite` | waveform bars |
| `ticker-scroll` | `38s linear infinite` | trust ticker tape |
| `stamp-thunk` | `460ms cubic-bezier(0.34, 1.2, 0.5, 1) 480ms backwards` | rubber-stamp entrance |
| `playhead-sweep` | `14s linear infinite` | rail playhead |
| Button sheen | `900ms cubic-bezier(0.22, 1, 0.36, 1)` | hover/focus sweep |
| Card lift | `260ms cubic-bezier(0.22, 1, 0.36, 1)` | hover translateY |
| Chapter h2 intro | `520ms` | per-palette heading flourish |
| Chapter children | `320-460ms` | `snap-in`, `margin-slide`, `tilt-in`, `drop-settle`, `wobble-in`, `ink-wipe`, `letter-rise` |

**Accessibility:**

- Respects `@media (prefers-reduced-motion: reduce)`.
- `html.reduce-motion` class forces `animation-duration` and `transition-duration` to `0.01ms`, disables `ticker-track`, `wave-bar`, and sheen.

Source: `trackdub.com/src/styles.css`, `trackdub.com/src/routes/index.tsx`.

## 10. Themes

### Website

- Default `:root` is the warm-cream "Autumn Harvest" light theme.
- `.dark` enables a slate-blue dark theme.
- Sections override the default with `chapter-paper`, `chapter-cream`, `chapter-ash`, `chapter-ink`, `chapter-burgundy`, `chapter-rust` utilities assigned by section ID.
- `shadcn/ui` base color is `slate` (`components.json`).

### Application

The application supports four persisted theme identifiers (`Trackdub.Contracts.AppThemeNames`):

- `dark`
- `light`
- `amber`
- `green`

Only `Dark` and `Light` resource dictionaries are currently present in the repository:

- `src/DubBench/Resources/DubBenchTheme.axaml` (Dark)
- `src/DubBench/Resources/DubBenchThemeLight.axaml` (Light)

`App.axaml` sets `RequestedThemeVariant="Dark"` and merges the icon dictionary plus the theme dictionaries via `ResourceDictionary.ThemeDictionaries`. It also applies the `FluentTheme` base style.

Source: `src/DubBench/App.axaml`, `src/Trackdub.Contracts/IStudioSettingsService.cs`, `trackdub.com/src/styles.css`, `trackdub.com/components.json`.

## 11. Implementation Notes

- The `DubBench` project in this repository is a benchmark harness, not the final Trackdub application shell. It does, however, define the current theme tokens and view patterns.
- The main app shell is shown on the marketing site (`app-shell-early-build.png`) but is not present in this public core repository.
- The website palette and application palette are not yet unified. Treat the values above as a baseline for reconciliation.
- The site uses Tailwind CSS v4 (`@import "tailwindcss"`, `@theme inline`, `@utility`) with `tw-animate-css`, not a traditional `tailwind.config.ts`.
- Custom self-hosted fonts replace Google Fonts to eliminate extra DNS/TLS/CSS round trips before text paints.

## 12. Sources

- `src/DubBench/App.axaml`
- `src/DubBench/Resources/BenchmarkIcons.axaml`
- `src/DubBench/Resources/DubBenchTheme.axaml`
- `src/DubBench/Resources/DubBenchThemeLight.axaml`
- `src/DubBench/Views/AudioPrepTabView.axaml`
- `src/DubBench/Views/BenchmarkWindow.axaml`
- `src/DubBench/Views/DubbingTabView.axaml`
- `src/DubBench/Views/LeaderboardTabView.axaml`
- `src/DubBench/Views/OnnxModelTabView.axaml`
- `src/DubBench/Views/PresetsTabView.axaml`
- `src/Trackdub.Cli/Tui/TrackdubTuiApp.cs`
- `src/Trackdub.Cli/Tui/TuiInlinePicker.cs`
- `src/Trackdub.Contracts/IStudioSettingsService.cs`
- `trackdub.com/components.json`
- `trackdub.com/package.json`
- `trackdub.com/public/fonts/`
- `trackdub.com/src/components/ui/button.tsx`
- `trackdub.com/src/routes/__root.tsx`
- `trackdub.com/src/routes/index.tsx`
- `trackdub.com/src/styles.css`
- `trackdub.com/` landing page, accessed 2026-07-26.

# Implementation Plan  -  G3: Cloud Egress Visibility & Consent

**Source:** [design-g3-cloud-egress-visibility.md](../specs/design-g3-cloud-egress-visibility.md)

**Prerequisite:** G5 Phase 1–2 (Contracts + Application Evaluate + Panel) must land first. G3 builds on ReadinessState, IPipelineReadinessService, and the readiness panel.

---

## Phase 1: Contracts  -  Cloud consent model (1 day)

Add consent tracking + exceptions.

**Files:**
- src/Trackdub.Contracts/Cloud/EgressType.cs
- src/Trackdub.Contracts/Cloud/CloudEgressDescription.cs
- src/Trackdub.Contracts/Cloud/CloudEgressConsentKeys.cs
- src/Trackdub.Contracts/Cloud/CloudEgressConsentException.cs

**Logic:**
- EgressType enum: Audio, Text, Media
- CloudEgressDescription record: ConsentKey, EgressType, ProviderName, DataDescription, Endpoint, PrivacyPolicyUrl
- CloudEgressConsentKeys static: constants for all 8 consent keys + Build(type, providerKey)
- CloudEgressConsentException: throw when engine lacks consent

---

## Phase 2: Application  -  Consent service (2 days)

Track and query consent state.

**Files:**
- src/Trackdub.Contracts/Cloud/ICloudEgressConsentService.cs
- src/Trackdub.Application/Cloud/CloudEgressConsentService.cs
- src/Trackdub.Application/Cloud/CloudEgressConsentCatalog.cs (static)

**Logic:**
- ICloudEgressConsentService: HasConsent(key), GetRequiredConsentKeys(stage, alias), SetConsentAsync(key, consented, ct)
- Impl reads/writes StudioSettings.CloudEgressConsents dict
- Catalog: static 8-entry table (audio:openai, audio:gemini, text:deepl, text:openai, text:gemini, text:elevenlabs, text:google, media:elevenlabs)

---

## Phase 3: Composition  -  Registration (1 day)

Wire consent service into app + SDK.

**Files:**
- src/Trackdub.Composition/CompositionRoot.cs

**Logic:**
- Register ICloudEgressConsentService → CloudEgressConsentService
- Inject into IPipelineReadinessService (for consent probes)
- Inject into cloud engine instances (ElevenLabsCloudTtsEngine, OpenAiCloudTranscriptionEngine, etc.)

---

## Phase 4: Application  -  Readiness integration (2 days)

Extend G5's readiness to check consent.

**Files:**
- (extend) src/Trackdub.Application/Pipeline/PipelineReadinessService.cs
- (extend) src/Trackdub.Contracts/Pipeline/ReadinessState.cs (add CloudEgressConsentRequired)

**Logic:**
- EvaluateAsync: for each cloud alias, check consent via ICloudEgressConsentService.HasConsent(key)
- If missing consent, return CloudEgressConsentRequired state
- G5's readiness panel already renders per-stage badges; consent badge + resolve action follow existing pattern

---

## Phase 5: App  -  Consent dialog (2 days)

Proactive consent prompt on model selection + pre-run backstop.

**Files:**
- src/Trackdub.App.Avalonia/Views/CloudEgressConsentDialog.axaml
- src/Trackdub.App.Avalonia/ViewModels/CloudEgressConsentViewModel.cs

**Logic:**
- Show when cloud alias selected (post-selection, proactive)
- Display egress type (Audio 🎙 / Text 📝 / Media 🎬), provider, data description, endpoint, privacy link
- [Allow] [Not now] buttons
- On Allow: SetConsentAsync(key, true) → persist to settings
- On Not now: keep selection, but stage blocked at pre-run (backstop catches it)

---

## Phase 6: Infrastructure  -  Defense-in-depth (1 day)

Assert guards in cloud engines.

**Files:**
- (extend) src/Trackdub.Infrastructure/Tts/ElevenLabsCloudTtsEngine.cs
- (extend) src/Trackdub.Infrastructure/Transcription/OpenAiCloudTranscriptionEngine.cs
- (extend) src/Trackdub.Infrastructure/Translation/DeepLCloudTranslationEngine.cs
- (extend) src/Trackdub.Infrastructure/Dubbing/ElevenLabsCloudDubbingEngine.cs

**Logic:**
- Each engine: at top of Synthesize/Translate/Transcribe/Dub, assert HasConsent(consentKey)
- Throw CloudEgressConsentException if absent (safety net; gate prevents this path)

---

## Tests

- HasConsent returns false for absent key; true for explicit true in dict
- GetRequiredConsentKeys returns correct key(s) for each cloud alias
- EvaluateAsync returns CloudEgressConsentRequired when consent absent
- audio:openai and text:openai are separate (both required for OpenAI ASR + TTS pair)
- ConsentDialog persists consent to StudioSettings
- Engine assert throws CloudEgressConsentException when consent absent

# Implementation Plan  -  G4: Run Progress & ETA

**Source:** [design-g4-run-progress-eta.md](../specs/design-g4-run-progress-eta.md)

---

## Phase 1: Contracts  -  Progress model (1 day)

Add per-stage progress record.

**Files:**
- src/Trackdub.Contracts/Pipeline/StageProgressReport.cs

**Logic:**
- StageProgressReport: StageName, PercentComplete?, ItemsComplete, TotalItems?, EstimatedTimeRemaining?, DisplayLabel
- Immutable record

---

## Phase 2: Application  -  ETA + Context (2 days)

Throughput tracker + extend context.

**Files:**
- src/Trackdub.Application/Pipeline/StageThroughputTracker.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/TranscriptGenerationContext.cs

**Logic:**
- StageThroughputTracker: Report(itemsComplete, totalItems) → TimeSpan? ETA
  - Simple ms/item avg; suppress before 200ms elapsed
- TranscriptGenerationContext: add `IProgress<StageProgressReport>?`; StageProgress field (optional, backward-compatible)

---

## Phase 3: Application  -  Stage progress wiring (3 days)

Add per-segment/region progress to handlers.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/StartTtsStageHandler.cs
- (extend) src/Trackdub.Application/Transcripts/TranslationOrchestrationService.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/AsrGenerationStage.cs

**Logic:**
- TTS handler: loop over segments, report per segment
- Translation service: loop over segments, report per segment
- ASR handler: loop over regions, report per region (use StageThroughputTracker)
- All use StageNames constant for StageName field

---

## Phase 4: SDK  -  Progress bridging (2 days)

Connect stage progress to pipeline events.

**Files:**
- (extend) src/Trackdub.Sdk/TrackdubDubbingEngine.cs

**Logic:**
- StageProgressAdapter: convert StageProgressReport → PipelineProgressEvent(kind=Progress)
- Thread `IProgress<StageProgressReport>` into TranscriptGenerationContext
- Download bridge (temporary until G5 lands): wrap ModelDownloadProgress → PipelineProgressEvent(Progress)
- Black-box stages (VAD/Diar/Sep): emit Progress event + optional periodic heartbeat

---

## Phase 5: App  -  VM progress fields (2 days)

Add progress bindings to view models.

**Files:**
- (extend) src/Trackdub.App.Avalonia/ViewModels/PipelineStageRowViewModel.cs
- (extend) src/Trackdub.App.Avalonia/ViewModels/PipelineRunViewModel.cs

**Logic:**
- PipelineStageRowViewModel: ProgressPercent, IsIndeterminate, EtaText fields
- PipelineRunViewModel: StagesComplete, StagesTotal, OverallElapsedText, OverallEtaText fields
- Subscribe to PipelineProgressEvent stream, update per-stage + overall on Progress kind

---

## Phase 6: App  -  AXAML bindings (2 days)

Add progress bars + ETA display to UI.

**Files:**
- (extend) src/Trackdub.App.Avalonia/Views/PipelineStagesView.axaml
- (extend) src/Trackdub.App.Avalonia/Views/RunConfigView.axaml

**Logic:**
- ProgressBar binds to PipelineStageRowViewModel.ProgressPercent
- IsIndeterminate binds to IsIndeterminate (for VAD/Diar/Sep)
- EtaText label shows "~23s remaining" or "00:01:42 elapsed"

---

## Phase 7: CLI  -  Progress rendering (1 day)

Update CliProgressReporter for Progress kind.

**Files:**
- (extend) src/Trackdub.Cli/CliProgressReporter.cs

**Logic:**
- Handle PipelineProgressEventKind.Progress
- Render as: [Stage   ] ████░░░░░░ 45%  12 / 26 segments  (~2m 15s)
- Black-box stages show "running…" with elapsed

---

## Tests

- StageThroughputTracker returns null before 200ms; finite ETA after
- StageProgressAdapter maps StageProgressReport correctly
- Translation emits N events for N segments
- TTS emits N events for N speakers' segments
- ASR emits M events for M regions
- VAD/Diar/Sep emit at least one Progress event (PercentComplete=null)
- PipelineRunViewModel.StagesComplete increments on Completed/Skipped

# Implementation Plan  -  G5: Consolidated Pipeline Readiness Gate

**Source:** [design-g5-readiness-gate.md](../specs/design-g5-readiness-gate.md)

---

## Phase 1: Contracts & core models (2 days)

Establish the read-only types and service interface. Map ReadinessState enum directly to Spec §5 (11 distinct states). Extend RunReadinessSnapshot with frozen RuntimeModelSelections.

**Files:**
- src/Trackdub.Contracts/Pipeline/ReadinessState.cs
- src/Trackdub.Contracts/Pipeline/StageReadiness.cs
- src/Trackdub.Contracts/Pipeline/IPipelineReadinessService.cs

---

## Phase 2: Application layer  -  Evaluate (3 days)

Build PipelineReadinessService. Evaluate per stage: artifact resumability, download/import/blocked status, cloud-key presence, voice-clone consent. Cache by (stage, selection-hash, artifact-fingerprint).

---

## Phase 3: Application layer  -  Provision (2 days)

Extend RuntimeModelSetupCoordinator. Batch DownloadRequired/ImportRequired stages by (ProviderKey, ModelId). Call RuntimeModelSetupWorkflow once. Demote per-stage Ensure* to non-interactive assert.

---

## Phase 4: SDK  -  pre-flight + Provision front-load (3 days)

Move provisioning fully up front in TrackdubDubbingEngine.RunPreFlightChecksAsync. Auto-download eligible stages. Fail fast with aggregated error listing all unmet stages. Delete stageProvisionedDuringExecution branch.

---

## Phase 5: App  -  readiness panel + live re-validation (4 days)

Build RunConfigPanelViewModel. Bind to draft selections. Debounce tier/lang/voice changes (300ms). Re-evaluate only affected stages (cache). Update per-stage badges live. Pre-run backstop: refuse Run while any stage is blocking.

---

## Phase 6: App  -  demote per-stage Ensure* (1 day)

Remove dialog calls from stage runners. Replace with non-interactive assert. Gate prevents reaching this assert path.

---

## Phase 7: Cleanup  -  diarization mismatch (1 day)

Verify SpeakerDiarizationStage calls CreateRuntimeSelections(snapshot), not CreateDefaultRuntimeSelections(). Both provision and execute see same snapshot.

---

## Risks

- Evaluate cost (disk + EP probes): mitigated by debounce + cache. Watch stale-cache if artifact store mutated outside context.
- SDK behavior change (longer pre-flight, fail-fast): confirm with CLI/API consumers.
- Cloud key validity: default to "present"; validate on explicit user action only.

# Implementation Plan  -  G7: Export Provenance & Attribution

**Source:** [design-g6-g7-attribution-provenance.md](../specs/design-g6-g7-attribution-provenance.md)

---

## Phase 1: Contracts + Domain (1 day)

Attribution types + extend StageRunRecord.

**Files:**
- src/Trackdub.Contracts/Artifacts/StageRunRecord.cs (extend)
- src/Trackdub.Contracts/Export/ExportManifestModel.cs (new)
- src/Trackdub.Contracts/Export/ExportAttributionRequirement.cs (new)

**Logic:**
- StageRunRecord: add ModelAlias? field (nullable, migration-safe)
- ExportManifestModel: Stage, ModelAlias, ModelId?, IsCloud, CloudProviderKey?, License?, RequiresAttribution
- ExportAttributionRequirement: ModelAlias, Stage, License, AttributionText?, SourceUrl?

---

## Phase 2: Application  -  Catalog (2 days)

Build attribution lookup from manifest.

**Files:**
- src/Trackdub.Application/Export/ModelAttributionCatalog.cs (static)
- src/Trackdub.Contracts/Export/IModelAttributionCatalog.cs

**Logic:**
- Catalog: keyed by model alias (lowercase), populated from bundled-models.manifest.json at compose time
- Find(alias) → ModelAttributionEntry?
- Cloud aliases map to RequiresAttribution=false, License="cloud-service-terms"

---

## Phase 3: Application  -  Manifest extension (2 days)

Extend ExportManifest + builder.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/ExportManifestModels.cs
- (extend) src/Trackdub.Application/Transcripts/ExportStageHandler.cs

**Logic:**
- ExportManifest: add `ContributingModels: IReadOnlyList<ExportManifestModel>`, `AttributionRequired: IReadOnlyList<ExportAttributionRequirement>`
- ExportManifestBuilder.Build(): call BuildContributingModels(request, catalog); filter RequiresAttribution=true → AttributionRequired

---

## Phase 4: Composition  -  Registration (1 day)

Wire catalog into app.

**Files:**
- src/Trackdub.Composition/CompositionRoot.cs

**Logic:**
- Register IModelAttributionCatalog → ModelAttributionCatalog (populated from manifest at startup)
- Inject into ExportManifestBuilder

---

## Phase 5: Infrastructure  -  Stage run persistence (1 day)

SQLite migration + write ModelAlias.

**Files:**
- (SQLite migration script)
- (extend) Stage run persistence code

**Logic:**
- Add ModelAlias column to stage_runs table (nullable)
- Extend stage run save to write ModelAlias from execution summary

---

## Phase 6: Composition  -  Stage handler wiring (1 day)

Pass ModelAlias on stage completion.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/Stages/AsrGenerationStage.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/SpeakerDiarizationStage.cs
- (extend) src/Trackdub.Application/Transcripts/TranslationWorkflow.cs
- (extend) src/Trackdub.Application/Transcripts/TtsWorkflow.cs

**Logic:**
- Each stage handler: extract ModelAlias from StageRuntimeExecutionSummary
- Call StageRunHelper.CompleteAsync(..., modelAlias)

---

## Phase 7: App  -  Attribution surface (2 days)

Show in export success view.

**Files:**
- (extend) src/Trackdub.App.Avalonia/ViewModels/ExportMixViewModel.cs
- (extend) src/Trackdub.App.Avalonia/Views/ExportMixView.axaml

**Logic:**
- ExportMixViewModel: bind ContributingModels, AttributionRequired
- Show per-stage model summary if any cloud provider used
- Show AttributionRequired section only if HasAttributionRequired=true
- Display license + HF link per model

---

## Tests

- ExportManifestBuilder empty StageModels → ContributingModels has TTS only (backward compat)
- With StageModels: all stages reflected; dedup by (Stage, ModelAlias)
- AttributionRequired contains only RequiresAttribution=true entries
- Cloud alias → IsCloud=true, RequiresAttribution=false, CloudProviderKey set
- ModelAttributionCatalog.Find("kokoro-onnx") → RequiresAttribution=true, Apache-2.0
- ModelAttributionCatalog.Find("whisper-tiny-onnx") → RequiresAttribution=false
- Export no attribution models → AttributionRequired empty; UI section hidden
- SQLite migration: null ModelAlias → manifest omits entry (no crash)

# MIGraphX phase 0  -  extension points

| Area | Location |
|------|----------|
| Provider enum | `src/Trackdub.Domain/Common/RuntimePlanning.cs`  -  `ExecutionProviderKind` |
| Milestone provider order | `src/Trackdub.Inference/Runtime/Planning/StageRuntimeRequirements.cs`  -  `Milestone5PlanningPolicy`, `StageRuntimeRequirementsCatalog` |
| Discovery | `src/Trackdub.Inference.Onnx/Runtime/Planning/OnnxExecutionProviderDiscovery.cs` |
| Bootstrap (platform) | `WindowsExecutionProviderBootstrapper`, `LinuxExecutionProviderBootstrapper` |
| WinML catalog | `WindowsMlExecutionProviderBootstrapper.Windows.cs`, `WindowsMlProviderRegistrationPolicy.cs` |
| Session options | `src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs`  -  `CreateSessionOptions` |
| Smoke tests | `OnnxExecutionProviderSmokeTester.cs` |
| Devices | `WindowsDeviceEnumerator.cs`, `LinuxDeviceEnumerator.cs` |
| Studio hardware overrides | `HardwareOverrideCatalog.cs`, `IStudioSettingsService.HardwareOverrides` |
| DI | `CompositionRoot.AddInference` |
| Strategy doc | [ADR-0002-windows-ml-provider-strategy.md](../decisions/ADR-0002-windows-ml-provider-strategy.md) |

# Olive recipe pilot

Trackdub can run [Olive](https://microsoft.github.io/Olive/) **recipe configs** for a small pilot set of bundled models instead of always using `olive auto-opt`.

## Scope

- **Automatic (Model Manager):** manifest `recipe_bindings` on pilot families `whisper-genai`, `whisper-onnx`, and `phi-genai`.
- **Fallback:** `auto-opt`, or bundled GenAI folder optimization when no recipe matches.
- **Developer override (ModelLab only):** `--olive-recipe-config <path>` runs `olive run --run-config <path>`.

End users still choose **execution provider** and **precision** only; there is no recipe picker in Model Manager.

## Recipes root

Recipe JSON files are **not vendored** in the repo. Set:

```powershell
$env:TRACKDUB_OLIVE_RECIPES_ROOT = "D:\Dev\olive-recipes"
```

Paths in `bundled-models.manifest.json` are relative to that root (for example `openai-whisper-tiny/cpu/whisper-tiny_cpu_int8.json`).

If the variable is unset or the config file is missing, optimization falls back to `auto-opt` and logs the reason in the optimization log.

## Manifest bindings

Optional `optimization.olive.recipe_bindings` entries:

| Field | Meaning |
|-------|---------|
| `provider` | `cpu`, `dml`, `cuda`, … (omit for any provider) |
| `precision` | `fp32`, `fp16`, `int8`, `int4`, … (omit for any precision) |
| `config_relative_path` | Path under `TRACKDUB_OLIVE_RECIPES_ROOT` |

Pilot models in the bundled manifest include Whisper GenAI (`openai/whisper-tiny`, `openai/whisper-base`), ONNX Whisper (`onnx-community/whisper-tiny`), and Phi GenAI (`microsoft/Phi-3.5-mini-instruct-onnx`).

## ModelLab override

```powershell
dotnet run --project src/Trackdub.Tools -- model-lab --olive-recipe-config "D:\Dev\olive-recipes\microsoft-Phi-3.5-mini-instruct\aitk\phi3_5_dml_config.json" ...
```

Unsupported in Model Manager UI; invalid override paths fail the run with an explicit error.

## Progress

Olive stdout lines matching `Step N/M` are also emitted as `[progress] Step N/M` for structured UI parsing; raw lines are still logged.

## Multi-component GenAI

When no recipe is selected and the model has multiple ONNX components in GenAI builder mode, optimization uses **shared bundled folder** `auto-opt` (`UseSharedComponentCache`) instead of per-component loops.

# Trackdub performance profiling report

Measured numbers live in [profiling-report.md](profiling-report.md). A 2026-06-13 scaffold copy used to be embedded here; it was removed so this file and the report could not drift.

# TensorRT RTX EP ABI plugin

TensorRT RTX is a runtime provider plugin, not a model and not a Windows ML catalog EP.

Trackdub registers the standalone ONNX Runtime EP ABI plugin before creating TRT RTX sessions, then selects the GPU `OrtEpDevice` whose EP name is:

```text
NvTensorRTRTXExecutionProvider
```

Do not accept `NvTensorRtRtxExecutionProvider` as the primary plugin identity. That spelling belonged to the deprecated Windows ML catalog wiring and must not be used for the standalone plugin route.

## Bundle layout

The plugin directory must contain these files together.

Windows:

```text
onnxruntime_providers_nv_tensorrt_rtx.dll
tensorrt_rtx_1_7.dll
tensorrt_onnxparser_rtx_1_7.dll
```

Linux (v0.4.0 cu13, installed through LinML.Catalog; SONAME symlinks are preserved on extract):

```text
libonnxruntime_providers_nv_tensorrt_rtx.so
libtensorrt_rtx.so
libtensorrt_onnxparser_rtx.so
libtensorrt_plugins.so
```

Companion libraries such as `tensorrt_plugins.dll` / `libtensorrt_plugins.so` are copied during install but are not part of the required readiness triple.

## Locator order

`TensorRtRtxPluginLocator` resolves the bundle directory in this order:

1. `StudioSettings.TensorRtRtxPluginDirectory`
2. `TRACKDUB_TRT_RTX_EP_DIR`
3. Default installed bundle under the Trackdub user data root (see below)

If the bundle is missing, use **Install** in Model Manager (downloads v0.5.0 cu13 on Windows; on Linux LinML.Catalog installs its pinned 0.4.0 wheel, persists the studio path, then registers), or run the dev fetch script.

## Fetch script (dev/CI)

From repo root on Windows or Linux x64:

```powershell
.\tools\dev\Fetch-TrtRtxEp.ps1
```

Optional `-InstallRoot` or `TRACKDUB_DATA_ROOT` overrides the user data root. The script verifies SHA-256 + size from `runtime/trt-rtx-ep.manifest.json`, extracts native libraries into the flat plugin directory, and prints `TRACKDUB_TRT_RTX_EP_DIR` for the current shell.

## Bundle channel (in-app + manifest)

Trackdub ships the EP ABI plugin through a pinned manifest, not the Windows ML catalog:

- **Manifest:** `runtime/trt-rtx-ep.manifest.json` (version `0.5.0`, linux-x64 package pinned to `0.4.0` to match LinML.Catalog, CUDA `cu13`, `trtRtxRuntimeVersion` `1.7.1` for Windows, per-RID archive URL + SHA-256 + size).
- **Composition copy:** `trt-rtx-ep.manifest.json` next to the app assembly (`Trackdub.Composition` `CopyToOutputDirectory`).
- **Downloader:** `TrtRtxEpBundleDownloader` verifies checksum/size, extracts required plugin files into `%UserDataRoot%/Providers/trt-rtx/<version>/cu13/<rid>/` (0.5.0 on win-x64, 0.4.0 on linux-x64).
- **In-app install:** Model Manager **Install** calls `ITrtRtxEpInstaller` after `NvidiaTensorRtRtx` license acceptance, persists `StudioSettings.TensorRtRtxPluginDirectory`, then registers via `ITensorRtRtxProviderBootstrap`.
- **Inference bootstrap:** registers an already-installed bundle only; it **never** downloads the EP bundle (same policy as WinML catalog EPs during session bootstrap).

## Download policy (install vs session)

| Path | May download TRT RTX EP bundle? |
|------|--------------------------------|
| Model Manager **Install** / bulk catalog install | Yes (after NVIDIA license acceptance) |
| CLI `trackdub providers trt-rtx install --accept-license` | Yes |
| Inference session bootstrap (`OnnxExecutionSessionFactory`) | **No** |
| Readiness / doctor / `providers trt-rtx status` | **No** |
| Benchmark harness (`BenchmarkTensorRtRtxBootstrap` with `allowProviderDownloads: true`) | Yes, only when license already accepted and bootstrap opts in |

Portable and per-user installs use the in-app or CLI install paths. A future MSI/EXE/MSIX wizard should call the same `ITrtRtxEpInstaller` after license acceptance (see [packaging/installer/README.md](../../packaging/installer/README.md)).

## Registration flow

`TensorRtRtxPluginService` owns plugin registration:

1. Verify NVIDIA hardware eligibility (Windows registry probe or Linux `/proc/driver/nvidia/gpus`).
2. Resolve and validate the plugin bundle directory.
3. Call `OrtEnv.Instance().RegisterExecutionProviderLibrary(...)` for `onnxruntime_providers_nv_tensorrt_rtx.dll`.
4. Enumerate `OrtEnv.Instance().GetEpDevices()`.
5. Require a GPU device named `NvTensorRTRTXExecutionProvider`.
6. Session creation appends that device through `SessionOptions.AppendExecutionProvider(env, devices, options)`.

TRT RTX must not call Windows ML `ExecutionProvider.TryRegister`, `EnsureAndRegisterCertifiedAsync`, or `SessionOptions.SetEpSelectionPolicy`.

## Runtime cache

Provider binaries and runtime cache are separate.

Provider bundle (installed by Model Manager or `Fetch-TrtRtxEp.ps1`):

```text
%LOCALAPPDATA%\Trackdub\Providers\trt-rtx\0.5.0\cu13\win-x64\   # Windows
~/.local/share/Trackdub/Providers/trt-rtx/0.4.0/cu13/linux-x64/   # Linux
```

Manifest: `runtime/trt-rtx-ep.manifest.json` (pinned NVIDIA GitHub release URLs + checksums).

Compiled TRT RTX runtime cache:

```text
%LOCALAPPDATA%\Trackdub\EngineCache\
```

Clear the engine cache after a GPU driver change, GPU swap, or TRT RTX EP version bump when inference fails with stale compiled engines:

```powershell
trackdub cache clear engines
trackdub doctor   # shows cache path and approximate size
```

Override cache roots:

```powershell
$env:TRACKDUB_ENGINE_CACHE_ROOT = "D:\TrackdubEngineCache"
$env:TRACKDUB_CACHE_ROOT = "D:\TrackdubCache"
```

`TRACKDUB_ENGINE_CACHE_ROOT` wins. Otherwise `TRACKDUB_CACHE_ROOT\EngineCache` is used. Otherwise Trackdub falls back to `%LOCALAPPDATA%\Trackdub\EngineCache`.

## Readiness states

Keep these states separate:

- Plugin directory resolved
- All required DLLs present
- Plugin registered with ORT
- `NvTensorRTRTXExecutionProvider` GPU device visible
- Model files downloaded and checksum verified
- Model/provider pair smoke-tested
- Pipeline stage ran and produced usable artifacts

Provider registration alone is not model readiness. A failed TRT RTX plugin route may fall back to DirectML, but the selected provider must be reported as DirectML, not TRT RTX.

## Smoke commands

Readiness/probe slices:

```powershell
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~TensorRtRtxPluginLocator|FullyQualifiedName~OnnxExecutionSessionFactory" --no-restore -m:1
```

Benchmark smoke on a Windows NVIDIA RTX machine with the plugin bundle available:

```powershell
$env:TRACKDUB_TRT_RTX_EP_DIR = "$env:LOCALAPPDATA\Trackdub\Providers\trt-rtx\0.5.0\cu13\win-x64"
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --model <model-id> --provider trt-rtx --runs 1 --format console
```

For explicit session testing, inspect the selected provider in benchmark output. Do not infer success from plugin registration logs alone.

## Benchmark / DubBench bootstrap

`Trackdub.Benchmarks` and DubBench share `BenchmarkOnnxExecutionBootstrap`:

- **WinML registry:** `ConfigureExecution(...)` before ONNX runs (same as `Trackdub.Benchmarks Program.cs`).
- **TRT RTX runner factory:** `CreateOnnxRunner()` wires `BenchmarkTensorRtRtxBootstrap` (plugin directory providers + optional license-gated bundle ensure).

Readiness paths (pick one):

1. **Model Manager**  -  Install after `NvidiaTensorRtRtxLicenseAccepted` in studio settings.
2. **Dev/CI fetch**  -  `tools/dev/Fetch-TrtRtxEp.ps1` (sets `TRACKDUB_TRT_RTX_EP_DIR`).
3. **Auto-download (opt-in)**  -  when `NvidiaTensorRtRtxLicenseAccepted` is true in `%LOCALAPPDATA%\Trackdub\settings.json` and benchmark/bootstrap allows provider downloads.

Headless operators:

```powershell
trackdub providers trt-rtx status
trackdub providers trt-rtx install --accept-license
trackdub doctor   # includes tensorrt-rtx-plugin probe row (no download)
```

DubBench ONNX runs call the same bootstrap before each benchmark invocation.

## Bumping TRT RTX EP release

When NVIDIA ships a new `TensorRT-RTX-EP-ABI` GitHub release:

1. Check which platforms the tag actually ships (v0.4.2 was Windows-only; v0.5.0 ships both). Run `tools/dev/Update-TrtRtxEpManifest.ps1 -Version <x.y.z> -TrtRtxRuntimeVersion <a.b.c> [-LinuxVersion <older tag>] [-CudaVariant cu13]` to refresh `runtime/trt-rtx-ep.manifest.json` (URLs, SHA-256, size, per-package `version` when a platform lags).
2. Inspect the archive: update `TensorRtRtxProviderConstants.BundledVersionWindows` / `BundledVersionLinux`, `BundledCudaVariant`, `BundledTrtRtxRuntimeVersion`, and the `tensorrt_rtx_<major>_<minor>.dll` / parser file names (plus `TrtRtxEpRequiredFiles` and the required-file list in `tools/dev/Fetch-TrtRtxEp.ps1`). `TrtRtxBundlePinTests` fails if constants and manifest drift. Check the plugin's imports (`dumpbin /dependents`): the cu13 Windows bundle links the CUDA runtime statically, the Linux bundle ships `libcudart.so.13`; adjust `TensorRtRtxCudaRuntimeBootstrap` if that changes.
   `BundledFingerprintVersion` (EP ABI version + TRT-RTX runtime version) keys smoke verdicts and EP-context stamps, so either bump invalidates them. It also names the TRT-RTX runtime cache directory (`EngineCache/trt-rtx/<fingerprint>`), so runtime caches from an older lineage are never fed to the new runtime.
3. Run `tools/dev/Fetch-TrtRtxEp.ps1` locally and verify `trackdub providers trt-rtx status`.
4. Run optional GPU smoke (`.github/workflows/trt-rtx-smoke.yml`) or `Trackdub.Benchmarks.DevHost --provider trt-rtx`.
5. Run `trackdub doctor` and advise users with stale engines to `trackdub cache clear engines` after upgrading the EP bundle.

## CI optional GPU tier

Default CI (`ci.yml`) stays unit/fake-backed. Optional smoke workflow: `.github/workflows/trt-rtx-smoke.yml`.

| Gate | Meaning |
|------|---------|
| Repository variable `TRACKDUB_TRT_RTX_SMOKE=true` | Enables the workflow job on self-hosted Windows with NVIDIA RTX |
| `TRACKDUB_TRT_RTX_EP_DIR` | Plugin directory after fetch (workflow sets from default install root) |
| `TRACKDUB_TRT_RTX_SMOKE=1` | Test attribute gate for optional integration tests (`RequiresTrtRtxFactAttribute`) |

The smoke job runs `Fetch-TrtRtxEp.ps1`, exports `TRACKDUB_TRT_RTX_EP_DIR`, then one `Trackdub.Benchmarks.DevHost --provider trt-rtx` invocation. A nonzero smoke exit fails the job.

## References

- [ONNX Runtime TensorRT RTX EP](https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html)
- [ONNX Runtime plugin EP usage](https://onnxruntime.ai/docs/execution-providers/plugin-ep-libraries/usage.html)
- [ADR-0002 Windows ML provider strategy](../decisions/ADR-0002-windows-ml-provider-strategy.md)

# Windows ML Phase 3: device policies

Optional advanced studio setting for catalog GPU sessions on Windows.

## Setting

- **Key:** `StudioSettings.WindowsMlExecutionDevicePolicy` (JSON enum name)
- **Default:** `Explicit` (unchanged production behavior)
- **UI:** Settings → Hardware → *Windows ML device policy (advanced)* (Windows only)
- **Restart:** Policy changes take effect on new sessions after save; Phase 4 evicts idle pooled sessions and invalidates the policy provider cache (restart still recommended if a leased session holds old options).

## Values

| Setting | ORT `ExecutionProviderDevicePolicy` | Behavior |
|---------|-------------------------------------|----------|
| `Explicit` | *(none)* | `GetEpDevices()` + `AppendExecutionProvider` (Phase 1–2 path) |
| `MaxPerformance` | `MAX_PERFORMANCE` | `SetEpSelectionPolicy` only; no per-EP append |
| `PreferNpu` | `PREFER_NPU` | Same |
| `MaxEfficiency` | `MAX_EFFICIENCY` | Same |
| `MinOverallPower` | `MIN_OVERALL_POWER` | Same |
| `DefaultRender` | `DEFAULT_RENDER` | Same, gated by capability probe (see below) |
| `MinPower` | `MIN_POWER` | Same, gated by capability probe (see below) |

Mapping: `WindowsMlExecutionDevicePolicyMapper` in `Trackdub.Inference.Onnx` (`#if WINDOWS`).

**Capability probe:** `DefaultRender` / `MinPower` resolve `DEFAULT_RENDER` / `MIN_POWER` by name via `Enum.TryParse` against the managed `Microsoft.ML.OnnxRuntime.ExecutionProviderDevicePolicy` surface, probed once per process. The pinned managed package version does not reliably predict when these members appear, so the probe requires **both** names to be present; if either is missing, `ApplyIfNeeded` returns early without calling `SetEpSelectionPolicy` (same as `Explicit`) instead of throwing.

## Rules

1. **Mutual exclusion:** Per session, either policy mode **or** explicit append  -  never both.
2. **Catalog only:** Policy applies to Windows ML catalog routes (`DirectMl`, `MIGraphX`). It does **not** select TensorRT RTX  -  TRT RTX uses the standalone EP ABI plugin (`trackdub providers trt-rtx`, Model Manager Install, or `Fetch-TrtRtxEp.ps1`).
3. **CPU / Kokoro:** CPU sessions never set policy; Kokoro CPU-only override unchanged.
4. **Planner / smoke:** Unchanged  -  `RuntimePlanFactory` and `OnnxExecutionProviderSmokeTester` still gate readiness.
5. **Fingerprint:** `BuildSessionOptionsFingerprint` includes policy key so pooled sessions do not mix explicit vs policy options.

## Seams

- Contracts: `WindowsMlEpDevicePolicyContracts.cs`, `IWindowsMlEpDevicePolicyProvider`
- Settings: `JsonStudioSettingsService.Normalize` coerces unknown enum to `Explicit`
- Composition: `StudioSettingsWindowsMlEpDevicePolicyProvider` → `OnnxExecutionSessionFactory.Initialize(bootstrapper, policyProvider)`
- Session factory: `OnnxExecutionSessionFactory.CreateSessionOptions(provider, devicePolicy, …)`

## Manual validation

After explicit matrix baseline on hardware:

1. `Explicit`  -  no regression vs Phase 2.
2. `MaxPerformance`  -  one VAD or ASR run on a **catalog** EP (`dml` / `migraphx`); log actual EP from benchmark or session metadata. Do not use this step to validate TRT RTX (see TRT RTX plugin smoke table in the stage matrix).
3. `PreferNpu` / `MaxEfficiency`  -  on Copilot+ PC if available; else N/A in matrix.
4. Change policy → restart → confirm new fingerprint / sessions.

**Benchmark harness:** `Trackdub.Benchmarks.DevHost --windows-ml-device-policy <name>` configures `OnnxModelBenchmarkRunner`. For Windows ML catalog/device-policy routes (`dml`, `migraphx`, `auto`), non-`Explicit` policies use `SetEpSelectionPolicy` only (no explicit catalog-device append). `trt-rtx` uses the standalone EP ABI plugin and ignores Windows ML device policy. CPU and native CUDA/TensorRT benchmark routes also never apply device policy.

## References

- [Select execution providers (device policies)](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers)
- [ADR-0002](../decisions/ADR-0002-windows-ml-provider-strategy.md)

# Windows ML Phase 4 closeout

Production closeout for Windows ML device policies (Phase 3) and ONNX runtime alignment on Windows.

**Status:** Implementation complete in repo; hardware matrix rows remain manual on Tony PC where noted.

## Workstream A0  -  Phase 3 review fixes (pre-merge)

| ID | Item | Status |
|----|------|--------|
| A0.1 | Cache `StudioSettingsWindowsMlEpDevicePolicyProvider` after first load | Done |
| A0.2 | Legacy pool fingerprint for `Explicit` / CPU (no policy key); catalog GPU non-explicit includes policy | Done |
| A0.3 | `TolerantWindowsMlExecutionDevicePolicyJsonConverter` | Done |
| A0.4 | Expanded `ShouldUseCatalogDevicePolicy` tests | Done |
| A0.5 | `ShowsWindowsMlDevicePolicyPanel` on `IDesktopPlatformService` | Done |
| A0.6 | Corrupt settings: log, timestamped `.corrupt` backup, defaults | Done |
| A0.7 | Thread-safe `OnnxExecutionSessionFactory.Initialize` (first call wins) | Done |

## Workstream A  -  Land Phase 3

- Build: `dotnet build Trackdub.sln`
- Tests: `dotnet test tests/Trackdub.Infrastructure.Tests tests/Trackdub.Inference.Tests`
- Windows TFM (local): `dotnet build Trackdub.sln -f net10.0-windows10.0.19041.0`

## Workstream F  -  ORT native alignment (P0)

**Problem:** Managed ORT 1.24.x vs WinML-bundled native 1.17.x caused API version 24 errors.

**Mitigation:** Resolver prefers managed-package `runtimes/win-*/native` before app base; benchmarks log managed ORT assembly version.

**Verify:**

```powershell
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --model <path> --provider trt-rtx --runs 1 --format console
```

## Workstream C  -  Pool eviction + policy cache

`InferenceSessionPool.EvictAllIdleAsync`, `IInferenceSessionPoolEvictor`, settings-save eviction + cache invalidation.

## Workstream D  -  Benchmark CLI

`--windows-ml-device-policy explicit|max-performance|prefer-npu|max-efficiency|min-overall-power`

(Additional policies have landed since this Phase 4 closeout; see [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md) for the current, canonical value list.)

Windows ML catalog/device-policy benchmark routes (`dml`, `migraphx`, `auto`) follow the same mutual-exclusion rule as the studio session factory: non-`Explicit` policies call `SetEpSelectionPolicy` only; explicit append runs only when policy is `Explicit`. `trt-rtx` uses the standalone EP ABI plugin and ignores Windows ML device policy. CPU and native CUDA/TensorRT routes ignore device policy.

## Workstream B  -  Hardware matrix (manual)

Update [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md) after F passes on hardware.

## Workstream E  -  ADR

[ADR-0002](../decisions/ADR-0002-windows-ml-provider-strategy.md) Phase 4 section.

## Related

- [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md)

# Windows ML Phase 5: catalog EP expansion (OpenVINO, QNN)

Internal checklist for Intel OpenVINO, Qualcomm QNN, AMD MIGraphX, and AMD VitisAI **Windows ML catalog** routes. TensorRT RTX is not a Windows ML catalog route anymore; it uses the standalone ORT EP ABI plugin documented in [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md).

## Relationship to existing paths

| Path | Enum | When used |
|------|------|-----------|
| Standalone OpenVINO | `ExecutionProviderKind.OpenVino` | Linux; optional Windows install via component downloader (`Infrastructure`). |
| WinML catalog OpenVINO (stub) | `ExecutionProviderKind.OpenVinoCatalog` | Future Windows catalog append; **not** the same as standalone OpenVINO. |
| WinML catalog QNN (stub) | `ExecutionProviderKind.Qnn` | Future Snapdragon / NPU catalog EP on Windows. |

Do not duplicate “GPU ready” semantics between standalone OpenVINO install state and catalog registration.

## Code seams

| Concern | Location |
|---------|----------|
| Catalog provider name constants | `Trackdub.Inference.Onnx/WindowsMl/WindowsMlCatalogProviderIds.cs` |
| Registration / bootstrap | `WindowsMlProviderRegistrationPolicy.cs`, `WindowsMlExecutionProviderBootstrapper.Windows.cs` |
| Discovery | `OnnxExecutionProviderDiscovery.cs`  -  stub kinds return **unavailable** |
| Session append | `OnnxExecutionSessionFactory.cs`  -  `NotSupportedException` until smoke path exists |
| Milestone probe order | `StageRuntimeRequirements.cs`  -  **unchanged in 5c** |

Stub marker: `#TODO(phase-5-catalog-ep)` in code; reference this doc and [ADR-0002 Phase 5](../decisions/ADR-0002-windows-ml-provider-strategy.md).

## Suggested smoke commands (when hardware exists)

Windows TFM:

```powershell
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --help
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --model <model-id> --provider dml
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --model <model-id> --windows-ml-device-policy PreferNpu
```

When QNN / catalog OpenVINO CLI aliases exist, add matrix rows here. For TRT RTX smoke commands, use the plugin doc instead of this catalog checklist.

## Matrix rows to add (when hardware exists)

| Hardware | Stage | Catalog EP to exercise | Notes |
|----------|-------|------------------------|-------|
| Intel GPU box | VAD / ASR | OpenVINO catalog | Distinct from standalone OpenVINO row |
| Snapdragon / Copilot+ PC | VAD / ASR | QNN | Pair with `PreferNpu` policy smoke; mark N/A if no NPU |

Update [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md) with pass/fail and **actual EP** from console  -  never infer from policy name alone.

## Enablement order (post-5c)

1. Confirm catalog ids in `WindowsMlCatalogProviderIds`.
2. Implement session append + smoke tester path.
3. Run stage matrix smoke on target hardware.
4. Update manifest `expected_runtime` and stage allow-lists if product commits.
5. Only then extend `Milestone5PlanningPolicy.SupportedProvidersThisMilestone`.

## References

- [ADR-0002](../decisions/ADR-0002-windows-ml-provider-strategy.md)
- [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md)
- [windows-ml-phase-4-closeout.md](windows-ml-phase-4-closeout.md)
- [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md)

# Windows ML stage provider matrix (Phase 2)

Internal audit companion for [ADR-0002](../decisions/ADR-0002-windows-ml-provider-strategy.md) stage catalog alignment.

## Planner intersection

`RuntimePlanFactory.GetOrderedProviders` orders providers as:

`Milestone5PlanningPolicy.SupportedProvidersThisMilestone` ∩ `AllowedProvidersThisMilestone` (or engine-family override).

Phase 2+ sets default stage allow-lists to `StageRuntimeRequirementsCatalog.DefaultOnnxStageAllowedProviders` (same sequence as milestone probe order) for ONNX spine stages. **`kokoro` engine-family override remains CPU-only** (ConvTranspose mechanical block).

Milestone probe order (2026-06): `TensorRTRtx` → `Migraphx` → `OpenVinoCatalog` → `Qnn` → `VitisAi` → `TensorRt` → `Cuda` → `OpenVino` → `DirectMl` → `Cpu`. Catalog EPs are in the intersection list; discovery + smoke-test gating still determine whether a route becomes `Verified`.

## Stage defaults (current)

|Stage|Default allow-list|Engine-family overrides|
|-|-|-|
|VAD|Milestone default| - |
|ASR|Milestone default|`whisper-genai` keeps GenAI-oriented list|
|Translation|Milestone default|`madlad`, `phi-genai`|
|Diarization|Milestone default| - |
|Separation|Milestone default|`spleeter`|
|OverlapRescue|Milestone default|`sepformer`|
|SpeechEnhancement|Milestone default|`deepfilternet3`|
|LipSync|Milestone default|`onnx-ctc-phoneme-aligner`|
|LipSynthesis|Milestone default|`latentsync-diffusion`|
|TextRefinement|Milestone default| - |
|TTS|Milestone default|**`kokoro` → CPU only** (ConvTranspose / DirectML incompatible)|

## Windows manual smoke checklist

Run on a machine with the relevant catalog EP installed before claiming GPU readiness in release notes. Record pass/fail in the PR or issue; planner must fall through on smoke failure (no fake readiness).

|Stage|Representative model|Catalog EP to exercise|Kokoro / CPU guard|
|-|-|-|-|
|VAD|`silero-vad`|TensorRT RTX (NVIDIA) or MIGraphX (AMD)| - |
|Diarization|Sortformer 4spk v2.1|Same| - |
|Translation|Any bundled `opus-*` ONNX pair|Same| - |
|TTS|`chatterbox-*`|Same|**Kokoro plan must stay CPU**|
|ASR|`whisper-tiny-onnx`|CUDA / DirectML / catalog per discovery|GenAI path separate|

### Windows smoke (Tony PC, RTX 5080, 2026-05-23)

|Stage|Result|Actual EP|
|-|-|-|
|VAD|pass|tensorrt-rtx (prior smoke); benchmark `dml` pass 2026-05-23|
|Diarization|pass|tensorrt-rtx|
|Translation|N/A|opus ONNX pair not present under `models/` on benchmark host (manifest aliases only)|
|TTS chatterbox|pass|tensorrt-rtx|
|Kokoro CPU guard|pass|cpu|
|ASR|pass|directml|

Suggested commands:

```powershell
dotnet build Trackdub.sln
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~RuntimePlanner"
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --help
```

## Manifest `expected_runtime` (Phase 2)

Bundled ONNX models use canonical token:

`windows-ml|onnxruntime-migraphx|onnxruntime-directml`

Legacy token `onnxruntime-directml|onnxruntime-migraphx` remains parseable in `ModelExpectedRuntimeFormatter` for older manifests.

This field is **governance / Model Manager hints only**; the runtime planner does not read `expected_runtime`.

## Device policy mode smoke (Phase 3)

Set **Settings → Windows ML device policy** to each non-default value, **restart Trackdub**, then run one stage per policy. Harness shortcut: `Trackdub.Benchmarks.DevHost --model silero-vad --provider dml --windows-ml-device-policy <name>` (catalog GPU; policy mode uses `SetEpSelectionPolicy`).

| Policy | Stage exercised | Pass/fail | Actual EP | Notes |
|--------|-----------------|-----------|-----------|-------|
| Explicit | silero-vad (benchmark) | pass | dml | Explicit append path |
| MaxPerformance | silero-vad (benchmark) | pending | dml/migraphx catalog route only | TRT RTX is no longer selected by Windows ML device policy; use the TRT RTX plugin smoke commands in `tensorrt-rtx-ep-abi-plugin.md`. |
| PreferNpu | silero-vad (benchmark) | pass | dml | No NPU on host; ORT fell back to DML |
| MaxEfficiency | silero-vad (benchmark) | pass | dml | |
| MinOverallPower | silero-vad (benchmark) | pass | dml | |

See [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md).

## TensorRT RTX EP ABI plugin smoke (separate from Windows ML policy)

TRT RTX is **not** a Windows ML catalog EP and is **not** selected by `WindowsMlExecutionDevicePolicy` (including `MaxPerformance`). Validate it with the standalone plugin route only.

| Stage | Representative model | Command / surface | Pass/fail | Actual EP | Notes |
|-------|---------------------|-------------------|-----------|-----------|-------|
| VAD | `onnx-community/silero-vad` | `Trackdub.Benchmarks.DevHost --provider trt-rtx` | pending | *pending local GPU run* | Requires NVIDIA GPU + plugin bundle |
| Headless status |  -  | `trackdub providers trt-rtx status` | pending | JSON `isOrtProviderListed` | Probe-only; no download |
| Headless install |  -  | `trackdub providers trt-rtx install --accept-license` | pending |  -  | License-gated bundle download |
| DubBench | same as benchmark | DubBench ONNX run after shared bootstrap | pending |  -  | Uses `BenchmarkOnnxExecutionBootstrap` |

Prerequisites: [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md) (Model Manager, `Fetch-TrtRtxEp.ps1`, or license-accepted auto-download). Optional CI: `.github/workflows/trt-rtx-smoke.yml` when repository variable `TRACKDUB_TRT_RTX_SMOKE=true`.

Suggested smoke:

```powershell
.\tools\dev\Fetch-TrtRtxEp.ps1
$env:TRACKDUB_TRT_RTX_EP_DIR = "$env:LOCALAPPDATA\Trackdub\Providers\trt-rtx\0.5.0\cu13\win-x64"
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --model onnx-community/silero-vad --provider trt-rtx --runs 1 --format console
trackdub providers trt-rtx status
```

## Phase 4 verification (2026-05-23)

Automated closeout: ORT native resolver prefers managed-package `runtimes/win-*/native` before app base; benchmark CLI `--windows-ml-device-policy`; pool eviction on settings save. See [windows-ml-phase-4-closeout.md](windows-ml-phase-4-closeout.md).

## Phase 5 catalog EP stubs (2026-05-23)

OpenVINO catalog + QNN + VitisAI documented in ADR-0002 Phase 5 and [windows-ml-phase-5-catalog-eps.md](windows-ml-phase-5-catalog-eps.md). They are included in `Milestone5PlanningPolicy.SupportedProvidersThisMilestone`; discovery may still report unavailable until installed, and smoke failure falls through to the next provider.
