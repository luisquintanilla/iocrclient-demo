---
name: new-layout
description: Add a new slide layout or component to the template. Writes a canonical snippet in layouts/, adds any needed talk-neutral styling to css/components.css using theme tokens, registers it in the layout catalog, and demonstrates it in slides.md. Use it when the existing layouts cannot express a slide you need.
---

# new-layout

Layouts are plain Markdown plus the component classes in `css/components.css`. This skill adds a new
one the right way, so it stays reusable, theme-driven, and documented.

## When to use

- A slide shape is not covered by the snippets in `layouts/`.
- You keep hand-rolling the same markup and want it named and reusable.

## What it does

1. Reads `layouts/README.md` and `css/components.css` to match existing conventions.
2. Adds any needed CSS as talk-neutral, token-driven classes.
3. Writes a canonical Markdown snippet to `layouts/<name>.md`.
4. Registers it in the `layouts/README.md` table.
5. Demonstrates it in `slides.md` so the showcase stays complete.

## Steps

### 1. Gather
Describe the slide shape: what goes where, and which existing layout is closest.

### 2. Reflect
Say back: "New layout [name]: [shape]. Closest existing layout: [name]. New classes needed: [list
or none]."

### 3. Interview (only what blocks the layout)
Ask only: what content the layout must hold, and whether it needs new styling or can reuse existing
components.

### 4. Analyze
Prefer reusing existing components. Add a class only when needed. New CSS must read theme tokens
(`--brand-*`, `--deck-*`), never hardcode colors, and stay talk-neutral. Keep the markup minimal so
it is easy to copy.

### 5. Emit
- If needed, add classes to `css/components.css` with a short comment.
- Write `layouts/<name>.md`: a leading `<!-- Layout: ... -->` comment, the snippet with TODOs, and a `Note:`.
- Add a row to the table in `layouts/README.md` (and the component table if you added a class).
- Add one slide to `slides.md` that uses the layout, so the tour covers it.
- Run `npm run build`, then preview the new slide.
- Close with: "Added layout [name]. It is in layouts/, the catalog, and the showcase deck."

## Rules

1. New styling is talk-neutral and token-driven. No hardcoded brand colors in components.
2. Every layout has a snippet file and a catalog row. Keep them in sync.
3. Demonstrate the layout in `slides.md`, or it is not really documented.
4. Hold the voice rules in `AGENTS.md` for any prose.

## Plain-checklist fallback (run by hand)

1. Copy the closest snippet in `layouts/` to `layouts/<name>.md` and adjust it.
2. If it needs styling, add a token-driven class to `css/components.css`.
3. Add a row to the table in `layouts/README.md`.
4. Add a slide in `slides.md` that uses the new layout.
5. Run `npm run preview` and check it.

---

**This skill grows the layout catalog without scattering one-off styles.**
