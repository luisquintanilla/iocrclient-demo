---
name: slide-ground
description: Turn a claim or slide point into a minimal runnable sample, run it, and embed the real output back into the slide. Use this when a technical slide needs proof from code, model output, Azure output, logs, or a CLI run.
---

# slide-ground

A slide-grounding ritual for technical talks. It turns "this is true" into "we ran it and here is what happened."

The loop is small: claim, sample, run, output, slide. It is an optional coach; the plain-checklist fallback below is the default when you run it by hand.

## When to use

Use this when a slide makes a technical claim that should be backed by a run:

- model behavior
- Azure or cloud service output
- CLI behavior
- library API shape
- performance or cost numbers
- generated files, logs, traces, or evaluations

Use it before a talk, after package changes, or any time a slide feels hand-wavy.

## What it does

1. Reads the claim and the target slide.
2. Writes or trims the smallest runnable sample that can check it.
3. Runs the sample from a clean shell.
4. Captures the real output.
5. Updates the slide with code, command, and output.
6. Leaves TODOs when a real run needs credentials or access.

## Steps

### 1. Gather (pull the slide and proof source)

Collect:

- The slide or claim we need to ground.
- The file path in `slides.md` or the section title.
- Existing sample files under `samples/`.
- Any known provider, model, deployment, endpoint, or CLI command.
- Required secrets or sign-in steps, without printing secret values.
- The exact command we expect to run.

If the claim is about AI output, identify the real model and provider. Prefer the same model the talk names. The template ships two ready paths: a local model via Ollama (`samples/02-ollama-chat.cs`, no token) and a cloud model via GitHub Models (`samples/03-github-models.py`, any GitHub token).

### 2. Reflect (say what would prove it)

Reflect back:

"This slide claims: [claim]. The smallest proof is: [sample]. The run command is: `[command]`. The output we need is: [shape]."

Call out gaps:

- No SDK installed.
- No token or sign-in.
- Sample depends on a live service.
- Output is nondeterministic.
- The claim is too broad to prove with one small run.

### 3. Interview (ask only what blocks the run)

Ask only the questions needed to proceed:

1. "Which real provider or model should this claim use?"
2. "Can we use the current token or signed-in account, or should we leave a TODO run note?"
3. "Is this output allowed to appear in slides?"
4. "Should the slide show raw output, trimmed output, or a link to the full capture?"

If the user is not available, make the safest local choice: write the sample, do not fake output, and leave a clear TODO that says what real run is needed.

### 4. Analyze (make the proof small)

Shape the sample:

- One claim per sample.
- One command to run it.
- Few dependencies.
- No hidden setup in the slide.
- No broad demo app when a small file proves the point.
- Output short enough to fit a slide.

For AI output:

- Keep the prompt in the sample.
- Log the model or deployment name when safe.
- Keep the raw response unless trimming is needed for readability.
- If trimming, save full output under `samples/output/` and note the trim in speaker notes.
- Never invent output. If the real run cannot happen, write `TODO run against [provider/model]`.

### 5. Emit (update files)

Update:

- `samples/<name>.*` with the minimal sample.
- `samples/output/<name>.txt` with captured output when a real run happened.
- `slides.md` with the claim, command, code snippet, and captured output.
- Speaker notes with setup or caveats.

Close with:

"Grounded: [slide title] via `[command]`. Output saved in `samples/output/<name>.txt`."

If the run did not happen, close with:

"Sample ready, output not grounded yet: run `[command]` after [credential/setup] and paste the real output."

## Rules

1. Real output only. Expected output is not proof.
2. Small beats big. Prefer one file and one command.
3. The claim controls the sample. Do not add features the slide does not need.
4. Keep secrets out of slides, logs, commits, and notes.
5. Show the run command near the output.
6. If a service call fails, capture the failure if it teaches the point. Otherwise fix the sample and rerun.
7. If access is missing, leave a TODO. Do not fake the result.
8. Keep Markdown canonical. Slides are edited in `slides.md`.

## Anti-patterns

- Pasting polished output that never came from a run.
- Hiding setup in a demo app when a tiny sample would work.
- Letting old output survive after code, prompt, model, or package changes.
- Showing an AI answer without the prompt that produced it.
- Turning a proof slide into a docs page.
- Printing tokens, keys, tenant IDs, or private data.

## Plain-checklist fallback (run by hand)

1. Write the slide claim in one sentence.
2. Create `samples/<name>.*` with the smallest sample that can check it.
3. Run it from the repo root.
4. Save output to `samples/output/<name>.txt`.
5. Paste the exact output into `slides.md`.
6. Add the run command to the slide or notes.
7. Re-run after any code, prompt, model, SDK, or package change.
8. If you cannot run it, leave a TODO that names the missing step.

---

**This skill keeps talks honest: claim, sample, run, output, slide.**
