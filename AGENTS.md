# AGENTS.md: Reveal Presentation Template

This is the operating manual for this template. It serves both people and AI coding assistants
(Copilot CLI, Claude Code, Cursor, and similar). The rule that shapes the whole repo: **if a
procedure is clear enough for a person to follow, an assistant can run it, and the other way
around.** So every procedure is written once and lives in one place. There is no separate human
guide and agent guide to drift apart.

This file is the index. It states the conventions and points at the skills. The skills hold the
step-by-step procedures, each with a plain-checklist fallback you can run by hand.

## What this is

A RevealJS presentation template with a reusable visual system, swappable themes, a layout
catalog, and a sample-output workflow that grounds technical claims on real runs. `slides.md` is
both the demo deck and a live tour of the template.

## Repo map

```
slides.md            The deck. Markdown is the single source of truth.
index.html           RevealJS bootstrap. Loads base + components + one theme.
build.mjs            Builds the static site into public/ (theme- and file-count-agnostic).
serve.mjs            Local preview server.
css/
  base.css           Structural styling. Consumes theme tokens. Theme-neutral.
  components.css      Reusable slide components (title, kicker, cols, output, stat, ...).
themes/
  default.css        The default theme: a block of design tokens (dark).
  light.css          A worked example of a second theme (light).
layouts/             Canonical copy-paste slide snippets, one per file + an index.
samples/             Small runnable proofs and their captured output/.
assets/              fonts/, img/, diagrams/.
.devcontainer/       Node + .NET + Azure CLI dev environment.
.github/
  skills/            The extension procedures (see Skills below).
  workflows/         GitHub Pages deploy.
```

## Conventions

- **Markdown is canonical.** Edit the deck in `slides.md`. Slides are separated by a line with
  just `---`. Vertical slides use `--`. Speaker notes start with `Note:`.
- **Themes are tokens, not structure.** A theme is one file in `themes/` that sets CSS custom
  properties. Never put brand colors in `css/base.css` or `css/components.css`; read tokens there.
- **Ground every technical claim on a real run.** Do not hand-write output. Run the smallest
  sample, save it under `samples/output/`, and paste the real result. If you cannot run it, leave a
  `TODO run against <provider>` line. This discipline is the point of the template.
- **Voice.** Use "we" and "you". Write short, active sentences and problem-first openings. Do not
  use em dashes. Avoid AI-giveaway words: delve, leverage, utilize, robust, comprehensive, pivotal,
  landscape, seamless, realm, tapestry.
- **No secrets in slides, samples, output, or commits.** Tokens come from the environment.

## Extension points and the skill for each

Each procedure lives once, in a skill under `.github/skills/`. Run the skill with an assistant, or
follow its plain-checklist fallback by hand. Same steps either way.

| You want to | Use | Reference |
| --- | --- | --- |
| Start a new talk from this template | `scaffold-new-talk` | resets `slides.md`, metadata, first sample |
| Back a claim with a real run | `slide-ground` | `samples/` + the `code-output` layout |
| Change or add a theme | `new-theme` | `themes/` token files |
| Add a slide layout or component | `new-layout` | `layouts/README.md` + `css/components.css` |
| Keep the prose on-voice | `voice-check` | the Voice convention above |
| Time the talk before delivery | `rehearse-timing` | reads `slides.md` |

## Build and preview

```bash
npm install      # once
npm run preview  # build + serve at http://localhost:8000 (press S for speaker view)
npm run build    # build only, into public/
```

The C# samples need the .NET 10 SDK. The Python sample needs Python 3 and a GitHub token. See
`samples/README.md`.

## Quick orientation for an assistant

1. Read this file and `layouts/README.md` and `samples/README.md`.
2. To make a change, find the matching skill in the table above and follow its steps.
3. Keep `slides.md` as the source of truth, ground claims on real runs, and hold the voice rules.
4. Run `npm run build` to check your change. Never invent sample output.

---

**This manual is for people and assistants alike. One repo, one set of procedures, run by hand or
by agent.**
