# Composition and degradation

The consumer has two independent seams:

1. `IDocumentExtractionClient` produces provider-neutral `DocumentExtractionPage` observations.
2. `PdfPigReader` decides when to use native PdfPig text or the injected extraction client.

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
