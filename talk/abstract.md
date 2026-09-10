# Abstract

**Preview 2 neutral shared-tree consumer validation**

This draft comparison tests a neutral shared `Document` on the authoritative non-generic MEDI
Preview 2 baseline. A credential-free two-page extraction runs through the built-in
`DocumentExtractionReader`, non-generic chunker/processor/pipeline contracts, a typed
`VectorStoreWriter<TRecord>`, real InMemory provider embeddings, and page-specific retrieval.

The proof also covers mixed `TextContent` and captionless-image `DataContent`, required token counts,
recursive provenance, exact Markdown isolation, PdfPig metadata, serialization round trip, and
independent use of `Microsoft.Extensions.Documents.Abstractions`.

Packages come only from implementation `6f7f3fa75d08599eb5005a0cd3db17d20694e1a8`.
Presentation `7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2` is evidence-only.

This evidence supports comparison with the bridge architecture. It does not declare a winner or
claim merge readiness, OCR quality, performance, or settled schema-evolution policy.
