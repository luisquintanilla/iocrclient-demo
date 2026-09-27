# Composition and degradation

The consumer has two independent seams:

1. `IDocumentExtractionClient` produces provider-neutral `DocumentExtractionPage` observations.
2. `PdfPigReader` decides when to use native PdfPig text or the injected extraction client.

`OcrPolicy.Never` stays local and native. `OcrPolicy.FallbackForEmptyPages` preserves native text and
requires an explicit page rasterizer for scanned-page OCR. `OcrPolicy.AllPages` uses the provisional
`Microsoft.Extensions.DataIngestion.DocumentExtractionReader`, which finalizes the page stream into
the shared Documents tree and maps one extraction page to one ingestion section.

The reader owns its PdfPig document and does not dispose the injected extraction client. The caller
owns the client lifetime. The deterministic proof checks this boundary with an in-memory client.

The bridge preserves page numbers and opaque page/evidence associations as metadata. It does not
fabricate geometry, confidence, provider payloads, or document identifiers. Document artifacts are
not silently discarded: the current ingestion model rejects them because it has no matching
ownership contract.

Provider failures remain failures. The only intentional degradation in the VLM adapter is a
schema/response-format incompatibility when requesting structured output. Cancellation, auth,
quota, request, transport, and timeout failures are never converted into an apparently successful
freeform result.
