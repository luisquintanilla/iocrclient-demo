# Samples

This directory contains the executable Preview 2 neutral comparison. Only outputs executed against
the exact six-package feed from implementation `704a3e44ef4d7b053748780549fc2c8e929a444b`
are committed.

> **DRAFT COMPARISON. DO NOT MERGE.**

## Credential-free evidence

```bash
../scripts/build-local-feed.sh
dotnet run 17-neutral-shared-tree-validation.cs
dotnet run --project documents-only/DocumentsOnly.csproj
```

`17-neutral-shared-tree-validation.cs` exercises:

- the built-in `DocumentExtractionReader` and ordered shared `Document` identity pass-through
- non-generic `IngestionPipeline`, `IngestionChunker`, `IngestionChunkProcessor`,
  `IngestionChunkWriter`, and `IngestionChunk`
- `AIContent` chunk content and required `TokenCount`
- exact two-page content, context, source IDs, page numbers, and no overlap-only terminal chunk
- `VectorStoreWriter<Preview2ChunkRecord>` and real InMemory provider embedding on upsert and query
- exact revenue and retention retrieval with persisted pages
- mixed `TextContent` and captionless-image `DataContent`, typed persistence, and serialization
- extraction-only Markdown/evidence isolation, PdfPig metadata, and recursive provenance
- explicit disclosure that page numbers persist and source node IDs do not persist by default

`documents-only/` references only `Microsoft.Extensions.Documents.Abstractions`. It constructs,
traverses, projects, and serializes the same tree, and checks the package dependency graph contains
only `System.Text.Json`.

Captured outputs:

- `output/17-neutral-shared-tree-validation.txt`
- `output/documents-only.txt`

## Exact package source

The closed architecture feed contains exactly six packages at
`10.8.0-preview2neutral.704a3e4`. Every package hash and nuspec repository/commit is verified by
`../scripts/build-local-feed.sh`.

- implementation/package source: `704a3e44ef4d7b053748780549fc2c8e929a444b`
- presentation evidence only: `7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2`
- common base: `f6ba2df16275bfc5eaf50aeb9327e2ec34ee8129`
- Preview 2 ancestor: `e124c123afeeda2f271f3b99a70eb3cfe187a471`

Published MEAI, provider SDK, vector store, and evaluation packages come from NuGet.org. They are not
part of the six-package architecture set.

## Provider and application compile tier

The four provider implementations live under `ocr-shape/Providers/`:

- `VisionLlmOcrClient`
- `FoundryMistralOcrClient`
- `AzureDocumentIntelligenceClient`
- `ContentUnderstandingClient`

They compile with every file-based sample and the hero solution. No provider is executed by the
credential-free validator, and no live-provider output is committed.

Azure credentials use `DefaultAzureCredential`; endpoints come from user secrets or environment
variables through `DemoConfig`. See `../docs/SETUP.md`.

## File map

| Path | Purpose |
| --- | --- |
| `01`-`05` | Direct provider construction and extraction API examples |
| `06` | Optional real Mistral PDF through the built-in neutral reader and Preview 2 chunker |
| `07` | Optional end-to-end RAG sample |
| `08` | PdfPig native or whole-document extraction modes |
| `09` | Image and URI extraction surfaces |
| `10` | OCR versus native-text evaluation harness |
| `11` | Structured vision transcription with safe exact-Markdown fallback |
| `12` | Non-generic Preview 2 pipeline, typed writer, and temporary SqliteVec store |
| `13` | Supported composition choices |
| `14` | Local Ollama GLM-OCR provider |
| `15` | Provider comparison harness |
| `16` | Streaming extraction updates |
| `17` | Credential-free authoritative Preview 2 neutral proof |
| `documents-only/` | Neutral package proof without MEDI or Document Extraction |

## Limitations

This evidence does not claim OCR quality, performance, live-provider behavior, merge readiness,
archive rebuild identity, or resolved schema-evolution policy. Existing vector collections may need
migration for page-number storage. `SourceNodeIds` persistence, immutable rewrite ergonomics,
cross-page hierarchy reconstruction, and sparse-table `O(cells^2)` overlap validation remain explicit
design considerations.

## Superseded evidence

Generic-main neutral evidence from consumer commit
`75bbb4195baf8ed92f702a6cfa1f02ec5360399e` is superseded. It remains reachable for audit but is not
presented as Preview 2 evidence.
