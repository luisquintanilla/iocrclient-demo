# Layouts

Each file here is one canonical slide layout: copy its Markdown into `slides.md`, then fill the
TODOs. Layouts are plain Markdown plus the component classes defined in `css/components.css`, so
nothing here needs a build step. The deck (`slides.md`) shows each one live.

Slides in `slides.md` are separated by a line with just `---`. Speaker notes start with `Note:`.

| File | Layout | When to use |
| --- | --- | --- |
| `title.md` | Title | The opening slide. Talk title, subtitle, speaker, date. |
| `section.md` | Section divider | Mark a new beat. A kicker plus one short heading. |
| `bullets.md` | Heading + bullets | The default content slide. Keep to three to five points. |
| `two-column.md` | Two columns | Compare, or pair a point with a callout. |
| `code-output.md` | Code + output | Ground a claim: code on the left, the real captured run on the right. |
| `big-stat.md` | Big stat | One number that earns the slide. |
| `quote.md` | Quote | A short, load-bearing quotation. |
| `image-full.md` | Full-bleed image | A diagram or screenshot that fills the slide. |
| `close.md` | Close | One clear next step. Do not recap. |

## Add your own layout

1. Copy the nearest existing snippet to `layouts/<name>.md`.
2. If it needs new styling, add a class to `css/components.css` (keep it talk-neutral, token-based).
3. Add a row to the table above.
4. Demonstrate it in `slides.md` so the showcase stays complete.

The `new-layout` skill (`.github/skills/new-layout/`) runs these same steps, by agent or by hand.

## Components used by these layouts

Defined in `css/components.css`, driven by the theme tokens in `themes/`:

| Class | What it is |
| --- | --- |
| `title-slide`, `title-rule`, `subtitle`, `byline` | Title-slide pieces. |
| `kicker` | Small uppercase label above a heading. |
| `cols`, `cols.narrow-left`, `col-left` | Two-column grid. |
| `cols.code-output`, `output`, `output-label` | Code-beside-output grounding block. |
| `callout` | Boxed aside with an accent border. |
| `run`, `slide-actions` | The run command shown near a result. |
| `lead`, `muted`, `small` | Emphasis and de-emphasis helpers. |
| `stat`, `stat-label` | Big single number. |
| `bleed-caption` | Caption over a full-bleed background image. |
| `foot` | Small footer line (for example a repo URL). |
