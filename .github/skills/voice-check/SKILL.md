---
name: voice-check
description: Check the deck and notes against the template voice rules. Flags em dashes and AI-giveaway words, and points at passive or padded lines. Use it before committing slides or before a talk, and after any large content edit.
---

# voice-check

A light editing pass that keeps the deck honest and human. It checks the prose in `slides.md`,
speaker notes, and the Markdown docs against the voice rules in `AGENTS.md`.

## When to use

- Before committing slide edits.
- Before a talk, as a final read.
- After an assistant drafts or rewrites content.

## The rules it enforces

- Use "we" and "you". Avoid "I" in shared material.
- No em dashes. Use a period, a comma, or parentheses.
- Avoid AI-giveaway words: delve, leverage, utilize, robust, comprehensive, pivotal, landscape,
  seamless, realm, tapestry.
- Prefer short, active sentences and problem-first openings.
- No secrets in slides, notes, or output.

## Steps

### 1. Gather
Collect the files to check: `slides.md` at least, plus `README.md`, `AGENTS.md`, and the docs under
`layouts/` and `samples/` if they changed.

### 2. Reflect
Say what will be checked and which rules apply.

### 3. Analyze (scan)
Run these scans from the repo root:

```bash
# em dashes
grep -rn '—' slides.md README.md AGENTS.md layouts samples

# AI-giveaway words (case-insensitive)
grep -rniE '\b(delve|leverage|utilize|robust|comprehensive|pivotal|landscape|seamless|realm|tapestry)\b' \
  slides.md README.md AGENTS.md layouts samples
```

Then read for passive voice, padded openings, and "I" in shared material. Tools find the easy
misses; a human read catches the rest.

### 4. Emit
- List each hit with file, line, and a suggested fix.
- Apply fixes when the intent is clear. Ask when a reword would change meaning.
- Re-run the scans until they return nothing.
- Close with: "Voice check clean: no em dashes, no giveaway words, [n] lines tightened."

## Rules

1. Do not change meaning to satisfy a rule. Reword, do not delete content.
2. Keep code blocks and captured output untouched. Output is evidence, not prose.
3. Leave proper nouns and quotations as they are.

## Plain-checklist fallback (run by hand)

1. Run the two `grep` scans above. Fix every hit.
2. Read each slide out loud. Cut padded openings and passive voice.
3. Replace "I" with "we" or "you" in shared material.
4. Confirm no tokens or private data appear anywhere.

---

**This skill keeps the deck reading like a person wrote it.**
