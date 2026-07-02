---
name: new-theme
description: Create a new theme for the deck from a brand or palette description. A theme is one token file in themes/. This writes the token file and wires it into index.html so the new look is a one-line swap. Use it to reskin a talk for an event, a company, or a personal brand.
---

# new-theme

A theme in this template is a single block of design tokens. This skill turns a brand or palette
description into a `themes/<name>.css` token file and points the deck at it. No structural CSS
changes.

## When to use

- A talk needs a different look (event brand, company colors, light or dark).
- You want a reusable theme to keep next to `default.css`.

## What it does

1. Reads `themes/default.css` to learn the full token contract.
2. Maps the brand or palette to each token.
3. Writes `themes/<name>.css`.
4. Updates the one theme `<link>` in `index.html` to point at it.
5. Builds to confirm the swap works.

## Steps

### 1. Gather
Collect the brand or palette: primary color, accent color, whether the deck is light or dark, and
any required fonts. If only a primary color is given, derive a sensible accent and surfaces.

### 2. Reflect
Say back the mapping: "primary [hex], accent [hex], [light or dark] surfaces, fonts [names]."
Note any token you are guessing.

### 3. Interview (only what blocks the theme)
Ask only: the primary and accent colors, and light or dark. Everything else has a safe default.

### 4. Analyze
Copy every token from `themes/default.css`. Keep the same names. Set readable contrast: heading and
text against `--deck-bg`, code against `--deck-code-bg`. If the deck is light, keep code surfaces
dark so the monokai syntax theme stays readable, or pick a light syntax CSS in `index.html`.

### 5. Emit
- Write `themes/<name>.css` with the full token block and a short header comment.
- Update `index.html`: point the theme `<link>` at `themes/<name>.css`.
- Run `npm run build`, then preview and check contrast on a code-output slide.
- Close with: "Theme [name] is live. Swap back by pointing the theme link at themes/default.css."

## Rules

1. A theme is tokens only. Do not add structural CSS to a theme file.
2. Set every token `default.css` defines, so no value falls back to nothing.
3. Check contrast on text, headings, links, and the code-output block.
4. If a theme needs a new font, add its `@font-face` in the theme file and ship the font in `assets/fonts/`.

## Plain-checklist fallback (run by hand)

1. Copy `themes/default.css` to `themes/<name>.css`.
2. Change the token values to your palette.
3. In `index.html`, point the theme `<link>` at `themes/<name>.css`.
4. Run `npm run preview` and check a title slide and a code-output slide for contrast.

---

**This skill keeps reskinning to one file and one line.**
