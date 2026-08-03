# Speaker outline + talking points

**One interface for every OCR engine: provider-neutral document parsing for .NET**
Luis Quintanilla · ~24 minutes (talk) or a 10-minute lightning cut

The through-line: **document parsing is a capability, not a vendor — and a capability is not a
pipeline stage.** Repeated beat: *one interface, any engine, swap with a line, and the page number
survives all the way to the answer.*

Every technical claim maps to a runnable sample under `samples/`, grounded in `samples/output/`. The
samples run on the REAL `dotnet/extensions` code (preview2 + #7588), packed into a local feed
by `scripts/build-local-feed.sh` — not a vendored copy.

---

## Time budget (24-min cut)

| # | Section | Slide | Sample | Time | Running |
|---|---------|-------|--------|------|---------|
| 0 | Primer: OCR in 90 seconds | Primer ×2 | - | 2 | 0:02 |
| 1 | Hook: the messy part of RAG | Problem | - | 2 | 0:04 |
| 2 | OCR is a capability, not a vendor | Pattern | - | 2 | 0:06 |
| 3 | The interface + the result shape | Interface, Result | `ocr-shape/` | 3 | 0:09 |
| 4 | The result grows: figures + UriContent | Result grows | `09` | 2 | 0:11 |
| 5 | Payoff: four engines, one loop | Payoff | `05` | 3 | 0:14 |
| 6 | Two archetypes (native vs vision) | Archetypes | `05` | 2 | 0:16 |
| 7 | Structured output from the vision path | Structured | `11` | 2 | 0:18 |
| 8 | The boundary + the stack + where it lives | Boundary, Stack, Where | `06` | 3 | 0:21 |
| 9 | Provenance survives the chunker | Provenance | `06` | 2 | 0:23 |
| 10 | The same seam, a second way (PdfPig #14) + composition | PdfPig, Composition | `08` | 3 | 0:26 |
| 11 | End to end: PDF to cited answer | End to end | `07` | 3 | 0:29 |
| 12 | Does it pay off? OCR vs PdfPig eval | Eval | `10` | 2 | 0:31 |
| 13 | The ask + close | Ask, Close | - | 1 | 0:32 |

Lightning cut (10 min): keep 0, 1, 5, 8, 11, 12, 13. Drop the interface deep-read, the archetypes and
structured-output slides, the figures slide, and the PdfPig/composition slides; mention page
provenance in one line on the end-to-end slide.

Buffer plan: sections 5, 8, 11, and 12 are the ones that must land. If short on time, talk-only
sections 3, 4, 7, 9, and 10 and let the captured output and diagrams carry them. Never skip the
live-run framing on 5, and always show the eval takeaway on 12.

The diagrams (`assets/diagrams/d1..d4`) are hand-authored and branded: D1 stack, D2 data-flow/boundary
(also the second primer slide), D3 composition, D4 eval harness. Each stands alone for reuse.

---

## 1. Hook (2 min)
- Open with the pain, not a story. Every RAG demo starts by turning a PDF into text, and that is the
  part that breaks. Tables collapse, pages blur together, and the moment you pick an engine your
  pipeline is welded to its SDK.
- Four engines, four wire protocols: Mistral OCR posts a data URL and reads `pages[]`; Document
  Intelligence async-polls an `AnalyzeResult`; Content Understanding makes you create an analyzer and
  poll again; a vision LLM wants a chat prompt.
- Promise: read the document through one interface, swap the engine with one line, and carry the page
  number all the way to a citable answer.

## 2. OCR is a capability, not a vendor (2 min)
- .NET already made this move twice. "Talk to a model" became `IChatClient`. "Turn text into vectors"
  became `IEmbeddingGenerator`. One interface, many providers, swap with a line.
- Reading a document is the same shape of problem and deserves the same seam. That seam is
  `IDocumentExtractionClient`, proposed in dotnet/extensions #7588, in `Microsoft.Extensions.AI`.

## 3. The interface + result shape (3 min) · *code: ocr-shape/*
- `ExtractAsync(Stream, mediaType) -> DocumentExtractionResult`. Stream in, normalized result out. `GetService` is
  the same escape hatch `IChatClient` has, so middleware and callers can still reach the concrete
  engine.
- `DocumentExtractionResult` is a list of `DocumentPage`, each with a `PageNumber`, `Markdown`, and `Tables`. Point at
  `DocumentPage.PageNumber`. It looks minor now; it is the thread we pull in sections 6 and 7.
- Honesty note: #7588 is not on nuget.org yet, so `scripts/build-local-feed.sh` packs the real branch
  into a local feed. The samples run on the actual `IDocumentExtractionClient` type, not a hand-written copy.

## 4. Payoff: four engines, one loop (4 min) · *sample: 05-one-loop-four-clients.cs*
- This is the slide that has to land. Build four clients (vision LLM, Mistral OCR, Azure DI, Content
  Understanding). The only provider-specific code is the four constructor lines.
- Then one loop, one call, identical for every engine. Show the captured table: vision returns 1
  blob (14249 chars); the three document-native engines each return 12 structured pages; DI even
  surfaces a table.
- Say it plainly: two of these engines are Microsoft's own (DI and Content Understanding), and they
  unify behind the same seam as everything else. No provider is privileged.
- All keyless via `DefaultAzureCredential`, all real, all reproducible.

## 5. Two archetypes (2 min) · *same run as section 4*
- Document-native engines (Mistral, DI, CU) take the whole PDF and return structured pages with native
  tables and confidence. Vision reads the page visually and returns one blob.
- The seam covers both. Your pipeline picks on fidelity and cost, not on API shape. Vision is a
  fallback transcriber you can choose deliberately, not a document reader you are stuck with.

## 6. The boundary + where it lives (3 min) · *sample: 06-medi-pipeline.cs*
- The question the demo is really about: **where does OCR stop and the pipeline begin?**
  - `IDocumentExtractionClient` = a **capability**: bytes -> `DocumentExtractionResult`. Knows nothing about pipelines.
  - `IngestionDocumentReader` (MEDI) = a **pipeline stage**: `ReadAsync -> IngestionDocument`. Knows
    nothing about which engine — or whether OCR is used at all.
  - `OcrDocumentReader : IngestionDocumentReader` = the **bridge**: one reader that composes ANY
    `IDocumentExtractionClient` and stamps the same metadata keys `PdfPigReader` uses. No per-engine reader, no
    `VisionOnly` flag — the engine is injected.
- Then the layering (Stack slide): the **abstractions** (`IDocumentExtractionClient`, `IngestionDocumentReader`) live
  in `dotnet/extensions`; the **concretes** (`VisionLMOcrClient`, `PdfPigReader`, processors) live
  in `CommunityToolkit/AI`. The core never privileges an engine.

## 7. Provenance survives the chunker (2 min) · *sample: 06-medi-pipeline.cs*
- A RAG pipeline chunks the reader's output. If you chunk the whole document at once, a chunk can span
  pages and the page number blurs — every citation becomes a guess.
- No new chunking API is needed. `OcrDocumentReader` emits **one section per OCR page** with
  `page_number` stamped; chunk each page-section on its own and every chunk carries its exact source
  page. Show whole-doc (page metadata lost) vs per-page (`page = 9` on each chunk) on the real MEDI
  `SectionChunker`.
- This is why `DocumentPage.PageNumber` mattered back in section 3 — the page model on the shipping `IDocumentExtractionClient`
  (#7588) is enough to stay citable. (We explored propagating element metadata through the chunker in
  #7516; it closed unmerged, and the demo doesn't need it.)

## 8. The same seam, a second way (2 min) · *sample: 08-pdfpig-reader.cs* · #13/#14/#15
- Most PDFs already carry a digital text layer — paying an OCR engine to re-read it is wasteful.
  PdfPig reads that native layer directly. The interesting shape is the hybrid: native text first,
  OCR **only** the pages that have none.
- CommunityToolkit #14 replatforms the PdfPig reader to **compose any `IDocumentExtractionClient`** for that per-page
  fallback; #15 is `VisionLMOcrClient`, an `IDocumentExtractionClient` over a vision chat model (it closes design
  issue #13). **The reader (#14) composes the client (#15)** — the boundary from section 6, in action.
- `OcrPolicy` is WHEN to OCR, not WHICH model: `Never` / `FallbackForEmptyPages` / `AllPages`. Show the
  captured run: the born-digital USGS fact sheet needs 0 OCR calls under `Never` and `Fallback`, and 1
  whole-document call under `AllPages` — and every path feeds the SAME chunker.
- Emphasize: the reader depends on `IDocumentExtractionClient` only, never a chat model directly.

## 9. End to end: PDF to a cited answer (3 min) · *sample: 07-e2e-rag.cs*
- Put it together: OCR the PDF (Mistral) -> `OcrDocumentReader` -> `SectionChunker` with page
  provenance -> retrieve -> answer grounded only on the retrieved chunks, each citable to its page.
- Show the captured run: 2 pages, 21 page-tagged chunks, retrieved from pages 0/1, final answer ends
  with `[page 0]`.
- Swap the one OCR line to any of the four engines and nothing else changes. The retriever is lexical
  on purpose; a real `IEmbeddingGenerator` + vector store drops into the marked slot unchanged. The
  point is the seam and the provenance.

## 10. The ask + close (1 min)
- The work spans two repos. `dotnet/extensions`: **#7588** (the `IDocumentExtractionClient` seam) — page provenance
  already rides the shipping API via per-page chunking, so the earlier chunk-propagation proposal
  (#7516) closed unmerged. `CommunityToolkit/AI`: **#13** (design), **#15** (`VisionLMOcrClient`,
  closes #13), **#14** (PdfPig reader composing any `IDocumentExtractionClient`).
- Every sample runs on the real preview2 + #7588 bits via `scripts/build-local-feed.sh` — this repo is
  the reproduction.
- One action: clone it, build the feed, and run `05-one-loop-four-clients.cs` against your own
  document. Then read the PRs and push back on the shape.
- Remember the one line: OCR is a capability, the reader is the bridge, and provenance makes answers
  citable.

---

## Demo notes
- Run `scripts/build-local-feed.sh` once before the talk (packs the real bits into `local-feed/`),
  then `az login`. All four accounts must have a Cognitive Services data-plane role for the signed-in
  identity.
- Endpoints come from `dotnet user-secrets` (UserSecretsId `iocrclient-demo`), with env vars as a
  fallback. Nothing sensitive is
  in the repo, the slides, or the captured output. Keep it that way when you re-capture.
- Mistral OCR can return a transient 503 on cold start. If it is slow live, fall back to the captured
  output for sections 7-9 and move on.
- Content Understanding needs a CU-supported region (West US, West US 3, Sweden Central, Australia
  East).
- `08-pdfpig-reader.cs` uses `PdfPig` from nuget.org for native text; the born-digital sample PDF makes
  the `FallbackForEmptyPages` path a no-op (0 OCR calls), which is the point — a scanned page would
  route to the injected engine.
