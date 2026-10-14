# Provisional Documents and extraction API notes

This validation branch uses the experimental integrated candidate
`10.10.0-dev.e2e.cf60fa88.1`: the corrected Documents/Extraction PR heads plus the reconciled
MEDI Preview 2 and #7488 design, migrated onto shared Documents. See
[`samples/README.md`](../samples/README.md) for exact source pins and import commands.
The complete 16-package manifest records source overlays, hashes and evidence. This is not a
public release, accepted upstream design, or the older `10.10.0-dev` composition bridge.

## Consumer contract

Providers implement:

```csharp
IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
    Stream document,
    string identifier,
    string? mediaType,
    DocumentExtractionOptions? options = null,
    CancellationToken cancellationToken = default);
```

Each page contains provider observations (`DocumentExtractionText`, `DocumentExtractionTable`,
`DocumentExtractionImage`, or an opaque `DocumentExtractionExtension`). The identifier is supplied
by the consumer. Provider observation IDs are local duplicate-detection keys, not durable document
IDs.

Consumers finalize explicitly:

```csharp
using DocumentExtractionResult result =
    await pages.ToDocumentExtractionResultAsync(identifier, cancellationToken);
```

Finalization sorts pages and observations, rejects duplicate page numbers and repeated observation
IDs, preserves equal text from distinct observations, and transfers document-owned artifacts to the
finalized `Microsoft.Extensions.Documents.Document`. Page and evidence references remain opaque
associations with their supplied order and multiplicity.

## Evidence boundaries

Shared Documents carries optional source-page frames, arbitrary polygons, annotations, confidence
with its meaning, table cells/spans/header associations and operation-scoped raw output. Adapters
must not fabricate absent page identity, geometry, OCR confidence or table structure. A layout
detector's score and a vision model's self-reported confidence are not OCR confidence.

Native objects and original result bytes have separate availability states. Capture is on by
default; `DocumentExtractionOptions` provides per-call model/format/provider options and raw
capture opt-outs. A reserialized SDK result, normalized ChatResponse or input PDF is not original
provider response data. Native borrowed objects must stay usable for their promised lifetime;
document-owned response artifacts are disposed with the shared document, once.

The MEDI `DocumentExtractionReader` now returns that shared `Document` directly. There is no
neutral-tree-to-old-IngestionDocument conversion and no legacy bridge package. Reader-first
pipeline entry points own documents they create; `ProcessAsync(Document, ...)` borrows the
caller's document. Callers retain ownership of injected extraction clients and input streams.

Chunks retain AIContent, context and token metadata plus `SourceElements`. Built-in chunkers
preserve contributing elements; consumers resolve page frames through those elements and their
ancestor sections, exact `DocumentPageReference` identifiers and annotation regions. Section
indexes, parsing identifier strings and full-page invented polygons are not page attribution.

The typed `VectorStoreWriter<TRecord>.CreateRecord` override persists safe citation metadata,
not raw provider payloads. The default `IncrementalIngestion=true` replaces old rows for the same
document with fresh row keys. `false` is append behavior, not a forced replacement switch.
Citation identity is a separate stable hash of source/content evidence, not the vector row GUID.

## Structured output

`VisionLlmOcrClient` opts into structured output through its provider-specific constructor. Only an
explicit schema, response-format, or structured-output incompatibility may fall back to freeform
transcription. Cancellation, authentication, quota, request, transport, and timeout failures
propagate. The deterministic proof exercises both categories without a live provider.

The in-memory Documents tree is canonical. No old serialized neutral-tree shape is frozen, and any
JSON round-trip remains an experimental, separately validated concern.

## Validation boundaries

The native E2E runner uses actual PdfPig, MEDI and reopened SqliteVec, with labelled deterministic
embedding/chat doubles. Live mode requires configured extraction, embedding and chat services
and explicit `--allow-live`; blocked services are not passes. Optional RT-DETR requires a pinned
compatible model and actual page rasterization. It detects regions, not scanned-page text or
table-cell structure, and does not imply Docling parity.

The hero source viewer verifies persisted source hashes and renders original PDF pages before
projecting known geometry. Unknown coordinate frames, rotated pages and crop offsets without a
verified transform produce explicit unsupported status. Passing projection tests is not a claim
that provider detection is accurate.
