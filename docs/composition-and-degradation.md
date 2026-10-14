# Composition and degradation

The consumer has two independent seams:

1. `IDocumentExtractionClient` produces provider-neutral `DocumentExtractionPage` observations.
2. `PdfPigReader` decides when to use native PdfPig text or the injected extraction client.

## Exchange fidelity versus processing policy

Documents and Extraction are exchange types and common workload seams. Providers
own recognition/inference quality; consumers choose table interpretation,
chunk budgets, retrieval and answer policy. A provider's imperfect transcription
must still be mapped literally. Fidelity means preserving what was supplied,
not correcting it or manufacturing missing structure.

| Provider | Table representation in this sample |
| --- | --- |
| DI and CU | Supplied dimensions, cells, roles, row/column spans and source regions remain structured. A logical table or figure spanning pages is emitted once on its first associated page, retaining all supplied page references and regions. |
| Mistral | Current `id`/`content`/`format` table bodies are retained literally, including HTML; legacy `markdown` bodies remain supported. No cell grid is inferred. |
| Vision | The sample's structured schema supplies dimensions and Markdown, not cells. Its confidence is labelled `model-self-reported`, not calibrated accuracy. |
| Native PdfPig | Native text blocks and source geometry, not table-cell recognition or OCR. |

For Mistral, a unique `[id](id)` page marker places a table without interpreting
its contents. A unique inline body is the legacy fallback. Bodies without an
unambiguous, non-overlapping placement are retained separately with
`placement=unplaced`; their appended position is not a reading-order claim.
The original page Markdown is unchanged. Table annotations retain supplied IDs,
format and cell availability even with raw capture disabled. Missing format
on a current `content` body remains unknown; it is not guessed from the text.

`CaptureRawOutput=false` applies to direct page streaming as well as finalized
Documents: native SDK/JSON objects are omitted from annotations and source
outputs. Normalized text, geometry and structured metadata remain available.
Markdown/HTML projections are not lossless interchange serialization; do not
use a plausible rendering as proof that all structured information survived.
In particular, a table's `GetMarkdown()` can return its supplied stored text,
whereas `ToMarkdown()` projects its current cell grid. A dimensioned grid with
no cell anchors can therefore produce empty rows even though its supplied text
is still available. Choose the appropriate view; clearing cells does not erase
the separately retained provider text. The exchange model does not infer whether
an empty grid means unavailable cells or a consumer's deliberate edit.
Deterministic SDK/HTTP replay evidence is recorded in
[`samples/ocr-shape/.testagent/status.md`](../samples/ocr-shape/.testagent/status.md).

## Composition and explicit degradation

`OcrPolicy.Never` stays local and native. `OcrPolicy.FallbackForEmptyPages` is an explicit
recognition policy using the caller's configured extraction client, not a hidden cloud fallback.
`OcrPolicy.AllPages` uses `Microsoft.Extensions.DataIngestion.DocumentExtractionReader`,
which finalizes the page stream directly into shared Documents. The caller owns injected clients.

The reader owns its PdfPig document and does not dispose the injected extraction client. The caller
owns the client lifetime. The deterministic proof checks this boundary with an in-memory client.

There is no old-model conversion. Shared source frames, geometry, confidence meaning, table
structure and operation-scoped raw output keep their original ownership and association rules.
Chunks carry source elements; page attribution resolves exact frame identifiers through those
elements, annotations and ancestor sections. A section index is not a source page number.

Optional local layout is a separate capability: compose `PdfPageRasterizer` with
`RtDetrLayoutDetector` into `PdfPigDocumentExtractionClient` and explicitly select
`AdditionalProperties["layoutMode"] = "rtdetr"` in per-call extraction options. The detector
requires a compatible, licensed model with a pinned SHA-256. It consumes actual page pixels,
not pictures of word bounding boxes. Missing assets fail explicitly; no disabled loader returns
null and claims that inference happened.

RT-DETR provides labelled regions and detection scores. PdfPig provides existing native text.
On image-only pages, local layout remains layout-only; no new OCR recognizer or table-cell
recognizer is included. Recognition requires an explicitly selected existing provider.

Provider failures remain failures. The only intentional degradation in the VLM adapter is a
schema/response-format incompatibility when requesting structured output. Cancellation, auth,
quota, request, transport, and timeout failures are never converted into an apparently successful
freeform result.

Vision PDF input is rendered one actual page at a time through the existing
PDFium renderer and sent to the configured chat client as PNG. This is not native
PDF support in ChatCompletions and does not install a local OCR engine. Source
page numbers come from the original PDF, including pages whose transcription is
empty; per-page outputs retain raw-policy and raster-transform provenance.
Unsupported crop/rotation mappings fail instead of fabricating coordinates.
