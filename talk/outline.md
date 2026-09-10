# Preview 2 neutral comparison run of show

## 1. Establish the baseline

- `data-ingestion-preview2` is authoritative.
- Pipeline, chunker, processor, writer, and chunk contracts remain non-generic.
- Chunk content is `AIContent`; `TokenCount` is required.

## 2. Show the neutral integration

- `IDocumentExtractionClient` returns an ordered shared `Document`.
- `DocumentExtractionReader` preserves that document identity.
- Extraction Markdown, geometry, confidence, and raw provider objects remain sidecars.

## 3. Run the two-page fixture

- Page 1: Quarterly Review, revenue paragraph, 2x2 table, image, provider note.
- Page 2: Appendix and retention policy.
- 256 tokens, zero overlap, exact content/context/tokens/source IDs/pages.

## 4. Persist and retrieve

- Typed `Preview2ChunkRecord` derives from `IngestionChunkVectorRecord`.
- Stock `VectorStoreWriter<TRecord>` invokes the configured `IEmbeddingGenerator<AIContent,...>`.
- Revenue retrieves page 1; retention retrieves page 2.
- Page numbers persist; source IDs do not persist by default.

## 5. Exercise the edges

- Mixed `TextContent` and captionless `DataContent`.
- Polymorphic content/page serialization round trip.
- Recursive/range provenance and no overlap-only terminal chunk.
- Exact Markdown isolation and PdfPig metadata.
- Documents-only consumer with only a System.Text.Json package dependency.

## 6. Compare without choosing

- Bridge handoff `a3033e0a`, source `c1913907`, consumer `aa55dfe6`.
- Neutral implementation `704a3e44`, presentation `7e5172fe`.
- Generic-main consumer `75bbb419` is superseded historical evidence.

## 7. State limitations

- Compile-only providers; no live OCR execution.
- No OCR quality, performance, merge-readiness, or archive rebuild identity claim.
- Open decisions: SourceNodeIds persistence, collection migration, immutable rewrites, cross-page
  hierarchy, schema evolution, and sparse-table `O(cells^2)` overlap checks.
