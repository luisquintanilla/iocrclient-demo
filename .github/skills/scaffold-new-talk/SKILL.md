---
name: scaffold-new-talk
description: Start a new talk from this template. Set the deck metadata, reset slides.md to a clean story skeleton built from the layouts, and wire a first runnable sample. Use this right after cloning or using the template, before you write any talk content.
---

# scaffold-new-talk

The "pick it up and start" ritual. It turns the showcase deck into an empty, correctly-shaped deck
for your talk, so you start from a clean story skeleton instead of a blank file.

## When to use

- Right after you clone the template or use it on GitHub.
- When you want to start a second talk in the same repo style.

## What it does

1. Reads the current `slides.md` metadata block and the `layouts/` catalog.
2. Replaces the showcase slides with a clean skeleton: title, problem, map, one grounded slide, close.
3. Sets the metadata (title, subtitle, speaker, event, date, repo).
4. Wires one first sample so the grounding loop works on day one.
5. Leaves clear TODOs for content, never invented content.

## Steps

### 1. Gather
Collect the talk title, subtitle, speaker name, event, and date. If any are unknown, leave the TODO.

### 2. Reflect
Say back the shape: "New deck titled [title] for [event], built from the title, problem, map,
grounded, and close layouts. First sample: [sample]." Call out anything missing.

### 3. Interview (only what blocks the scaffold)
Ask only: the talk title, the one claim you most want to ground first, and the stack of the first
sample (.NET, Python, shell, other).

### 4. Analyze
Pick the smallest set of layouts for the story skeleton. Default to: `title`, `bullets` (problem),
`bullets` (map), `code-output` (the first grounded claim), `close`. Do not add layouts the story
does not need yet.

### 5. Emit
- Rewrite `slides.md`: set the metadata block, then assemble the skeleton from `layouts/` snippets.
- Create one starter sample in `samples/` (or reuse `samples/01-hello-dotnet.cs`) and capture its
  output so the grounded slide is real from the start.
- Run `npm run build` to confirm the deck builds.
- Close with: "Scaffolded [title]. Edit slides.md, then ground your first claim with slide-ground."

## Rules

1. Never invent talk content. Leave TODOs.
2. Keep the metadata block intact and complete.
3. The first grounded slide must cite a sample that actually ran.
4. Hold the voice rules in `AGENTS.md`.

## Plain-checklist fallback (run by hand)

1. Edit the metadata block at the top of `slides.md` (title, subtitle, speaker, event, date, repo).
2. Delete the showcase slides below it.
3. Paste these snippets from `layouts/`, in order: `title`, `bullets`, `bullets`, `code-output`, `close`.
4. Fill the TODOs in the title, problem, and map slides.
5. Run one sample in `samples/`, save its output, and paste it into the `code-output` slide.
6. Run `npm run preview` and walk the skeleton once.

---

**This skill gives every talk the same clean starting line.**
