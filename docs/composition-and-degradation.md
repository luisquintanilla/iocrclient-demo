# Composition & graceful degradation — `PdfPigReader`'s two seams

> Guidance, not machinery. There is no `FallbackOcrClient` and no `PdfPigHeuristicOcrClient`.
> Graceful degradation is how you **compose** the building blocks that already exist.

The demo's `PdfPigReader` (a MEDI `IngestionDocumentReader`, mirroring CommunityToolkit/AI PR #14)
has **two clean, independent pluggable seams**. Every extraction strategy in this talk is a point in
the space those two seams span — you don't reach for a new type, you pick the rung your document and
environment support.

```
PdfPigReader : IngestionDocumentReader
  seam 1  IPageSegmenter          — HOW to segment a page
  seam 2  IOcrClient + OcrPolicy   — WHEN / whether to OCR
```

Both seams default to the cheapest, most local option, so the zero-argument reader is a pure
native-text reader with heuristic layout — no ML, no network, no `IChatClient`.

---

## Seam 1 — `IPageSegmenter`: HOW to segment a page

PdfPig exposes page layout analysis as an `IPageSegmenter`. Two implementations bracket the range:

| Segmenter | Cost | Where it comes from |
| --- | --- | --- |
| `DefaultPageSegmenter` | local, zero ML, zero network | PdfPig (heuristic, docstrum-style) |
| `OnnxPageSegmenter` | local ML (an RtDetr layout model) | CommunityToolkit/AI **PR 3**, `PdfPig.OnnxLayoutAnalysis` |

They implement the **same interface**, so swapping the ML layout model in is a one-argument change to
the same reader — the pipeline downstream never notices:

```csharp
// heuristic (default)
var reader = new PdfPigReader(pageSegmenter: DefaultPageSegmenter.Instance);

// ML layout — same seam, richer segmentation (PR 3 prior art)
var reader = new PdfPigReader(pageSegmenter: new OnnxPageSegmenter(/* model */));
```

`OnnxPageSegmenter` is the documented rung: PR 3 already built this exact seam. If the ONNX model is
present in your environment, plug it in; if not, you **degrade inward** to `DefaultPageSegmenter`.

## Seam 2 — `IOcrClient` + `OcrPolicy`: WHEN / whether to OCR

OCR is an injected *enrichment*, not the reader's identity (which is why the class stays
`PdfPigReader`, not `PdfPigOcrReader`). `OcrPolicy` decides **when** the injected `IOcrClient` runs;
the client decides **how** (Mistral OCR, Azure DI, Content Understanding, a vision-LLM — swap on one
line):

| Policy | Behavior |
| --- | --- |
| `OcrPolicy.Never` | native PdfPig text only; the `IOcrClient` is never touched |
| `OcrPolicy.FallbackForEmptyPages` | native first; OCR **only** pages with no digital text |
| `OcrPolicy.AllPages` | hand the whole document to the `IOcrClient` (document-native archetype) |

---

## The spectrum (cheapest / local → richest / service)

Composing the two seams gives a single ordered spectrum:

| Rung | seam 1 | seam 2 | Needs |
| --- | --- | --- | --- |
| 1. native + heuristic layout | `DefaultPageSegmenter` | `Never` | nothing (local) |
| 2. native + ML layout | `OnnxPageSegmenter` (PR 3) | `Never` | a local ONNX model |
| 3. native + OCR the scanned pages | `DefaultPageSegmenter` | `FallbackForEmptyPages` | an `IOcrClient` (only where needed) |
| 4. whole-document OCR | — | `AllPages` | an `IOcrClient` (every page) |

**"Degradation" is choosing the rung you can run and falling inward when a rung's model or service
isn't available.** No ONNX model? Rung 2 → rung 1. No OCR endpoint? Rung 3 → rung 1. The reader is the
same; only the composed seams change. `samples/13-composition.cs` runs rungs 1–4 over one document and
falls inward automatically when a rung's dependency is missing.

## Why composition, not a new type

A `FallbackOcrClient` or a `PdfPigHeuristicOcrClient` would bake one policy into a type and hide the
seams. Keeping the two seams open — `IPageSegmenter` and `IOcrClient` + `OcrPolicy` — means:

- the **caller** owns the strategy (which rung), the **reader** owns the mechanism;
- new layout models (PR 3's `OnnxPageSegmenter`) and new OCR engines drop into existing seams with
  zero new reader types;
- "degrade gracefully" is a runtime composition decision, not a class hierarchy.

This is the same "reader owns WHEN, client owns HOW" boundary from the core talk, extended with a
second HOW seam for page layout. The building blocks already exist; the spectrum is how you compose
them.

## Recommendation for CommunityToolkit/AI #14

Fold the OCR path into the shipped `PdfPigReader` as an **optional injected `IOcrClient` + `OcrPolicy`**
(replacing a `PdfReadingMode.VisionOnly`-style flag), and keep `IPageSegmenter` injectable so PR 3's
`OnnxPageSegmenter` composes without a new reader type. One reader, two seams, OCR as enrichment — the
name `PdfPigReader` stays accurate because under `OcrPolicy.Never` it is exactly that.
