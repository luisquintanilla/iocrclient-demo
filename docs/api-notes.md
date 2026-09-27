# Provisional Documents and extraction API notes

This repository consumes an experimental, unmerged stack from `dotnet/extensions`:

- Documents: `d35d5cb74695c4ba970d6ad198006b84354badc0`
- DocumentExtraction: `2d28dfd57712b55f941fbb1dbf41311eeaee8c8b`
- composition bridge: `8ae3d2436d105c8449a712d58618ff444b617f7b`

The local feed is rebuilt at `10.10.0-dev` by `scripts/build-local-feed.sh`. These are provenance
pins, not a public package release.

## Consumer contract

Providers implement:

```csharp
IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
    Stream document,
    string identifier,
    string mediaType,
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

The shared result does not invent provider geometry, confidence, raw payloads, credentials, signed
URLs, or grounding claims. Adapters may retain provider-specific richness internally, but the
neutral tree exposes only fields represented by the provisional contract. Images may carry optional
content; the credential-free proof reports presence only and never writes document-derived bytes.

The DataIngestion bridge maps the finalized tree into one section per extraction page. Page numbers
and opaque references become metadata. Unsupported document artifacts are rejected rather than
silently dropped.

## Structured output

`VisionLlmOcrClient` opts into structured output through its provider-specific constructor. Only an
explicit schema, response-format, or structured-output incompatibility may fall back to freeform
transcription. Cancellation, authentication, quota, request, transport, and timeout failures
propagate. The deterministic proof exercises both categories without a live provider.

The in-memory Documents tree is canonical. No old serialized neutral-tree shape is frozen, and any
JSON round-trip remains an experimental, separately validated concern.
