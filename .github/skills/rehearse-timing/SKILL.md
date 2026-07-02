---
name: rehearse-timing
description: Estimate how long the talk runs and where it will overrun. Reads slides.md, counts speakable words per slide (body plus speaker notes), applies a speaking rate, and produces a per-slide and total run sheet. Use it before a talk to find slides to cut or split.
---

# rehearse-timing

A quick projection of talk length from the deck itself. It is an estimate to plan against, not a
substitute for a real out-loud run. It flags the slides most likely to run long.

## When to use

- Before a talk, to check the deck fits the slot.
- After a content edit, to see the new length.
- When deciding what to cut to make time.

## What it does

1. Splits `slides.md` into slides on the `---` separators.
2. Counts speakable words per slide: body text plus the `Note:` block, ignoring code blocks and HTML.
3. Applies a speaking rate to estimate seconds per slide and a total.
4. Flags slides over a per-slide budget and reports the total against the slot.

## Steps

### 1. Gather
Get the target length (for example 30 minutes) and the speaking rate. Default to 130 words per
minute, which is a calm technical pace. Demos add real time that word count cannot see, so note
each demo slide separately.

### 2. Reflect
Say back: "Estimating [deck] at [rate] wpm against a [length] slot. Demos counted separately."

### 3. Analyze (estimate)
Run this from the repo root to get words and a time estimate per slide:

```bash
node -e '
const fs=require("fs");
const wpm=Number(process.env.WPM||130);
const raw=fs.readFileSync("slides.md","utf8").replace(/^<!--[\s\S]*?-->/,"");
const slides=raw.split(/\n---\n/);
let total=0;
slides.forEach((s,i)=>{
  const text=s.replace(/```[\s\S]*?```/g,"").replace(/<[^>]+>/g,"");
  const words=(text.match(/[A-Za-z0-9_’\x27-]+/g)||[]).length;
  const secs=Math.round(words/wpm*60);
  total+=secs;
  const flag=secs>120?"  <-- long":"";
  console.log(`slide ${String(i+1).padStart(2)}: ${String(words).padStart(4)} words  ~${String(secs).padStart(3)}s${flag}`);
});
const m=Math.floor(total/60), sec=total%60;
console.log(`total: ~${m}m ${sec}s at ${wpm} wpm (demos not included)`);
'
```

Set a different pace with `WPM=150 node -e '...'`.

### 4. Emit
- Produce the per-slide table and the total.
- Compare the total plus demo time against the slot. Flag the overrun if any.
- Suggest the smallest fix: cut or split the longest slides, or trim notes.
- Close with: "Estimated [total] against a [slot] slot. Longest slides: [list]. Suggested cuts: [list]."

## Rules

1. This is an estimate. A real out-loud run is the source of truth.
2. Count demos separately. Word count cannot see a live run.
3. Do not edit slide content here. Report and suggest; let the speaker decide.

## Plain-checklist fallback (run by hand)

1. Read each slide out loud and time it with a stopwatch.
2. Write the seconds next to each slide.
3. Add a real estimate for each demo.
4. Sum it. If it is over the slot, cut or split the longest slides first.

---

**This skill turns a deck into a run sheet so the talk fits its slot.**
