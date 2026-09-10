<!--
title: Preview 2 neutral shared-tree validation
subtitle: Consumer evidence for architecture comparison
speaker: Luis Quintanilla
date: 2026-09-09
repo: https://github.com/luisquintanilla/iocrclient-demo
-->

<div class="title-slide">

<span class="kicker">DRAFT COMPARISON / DO NOT MERGE</span>

# Preview 2 neutral shared-tree validation

<p class="subtitle">Consumer evidence for architecture comparison</p>

<div class="title-rule"></div>

<p class="byline">Implementation 6f7f3fa7 · Presentation 7e5172fe</p>

</div>

Note:
The implementation SHA is the only package source. The presentation SHA is evidence-only.

---

<span class="kicker">Preview 2 baseline</span>

## Non-generic where the pipeline composes

```csharp
IngestionPipeline pipeline;
IngestionChunker chunker;
IngestionChunkProcessor processor;
IngestionChunkWriter writer;
IngestionChunk chunk;

AIContent content = chunk.Content;
int tokenCount = chunk.TokenCount; // required, positive
```

<div class="slide-actions">
<span class="run">data-ingestion-preview2 ancestor e124c123</span>
</div>

Note:
The typed extension point is the writer record, not the pipeline or chunk.

---

<span class="kicker">Neutral path</span>

## One shared semantic value

```csharp
using IDocumentExtractionClient client =
    new FixtureExtractionClient(result);
var reader = new DocumentExtractionReader(client);

using var writer =
    new VectorStoreWriter<Preview2ChunkRecord>(collection);
using var pipeline =
    new IngestionPipeline(reader, chunker, writer);
```

<div class="callout">
The built-in reader passes the ordered shared `Document` identity through unchanged.
</div>

---

<span class="kicker">Deterministic run</span>

## Exact chunks, tokens, sources, and pages

<div class="output">
<span class="output-label">samples/output/17-neutral-shared-tree-validation.txt</span>

```text
Contracts: pipeline/chunker/processor/writer/chunk=non-generic content=AIContent TokenCount=required
Shared: pages=1,2 nodes=17 table=2x2 image=4B unknown-kind=paragraph
Page refs: producer-order=3,1,3 multiplicity=preserved inferred=none sentinel=none
Opaque/non-text: logical_kind=provider.unknown-chart schema=4 payload=roundtrip page_refs=2,1,2 text=excluded image=roundtrip
Chunks: count=2 types=TextContent|TextContent tokens=28|8 pages=1|2 source_ids=13|2
```
</div>

Note:
The sample uses 256 maximum tokens and zero overlap. It also checks recursive/range provenance and
that the token chunker does not emit an overlap-only terminal chunk.

---

<span class="kicker">Stock writer</span>

## Typed records and real provider embeddings

```csharp
sealed class Preview2ChunkRecord : IngestionChunkVectorRecord
{
    [VectorStoreVector(4)]
    public override AIContent? Embedding => Content;
}
```

<div class="output">

```text
Writer: typed=Preview2ChunkRecord stored=2 pages=1|2
        SourceNodeIds=not-persisted
        embeddings=TextContent,TextContent
Retrieval: revenue=page1:$12M retention=page2:unchanged
```
</div>

Note:
The stock writer persists page numbers. It does not persist source node IDs by default.

---

<span class="kicker">Mixed AIContent</span>

## Text and captionless image both persist

<div class="output">

```text
Mixed: chunks=TextContent(2)|DataContent(1)
       stored=2 roundtrip=true
       embeddings=TextContent,DataContent
```
</div>

The proof serializes and restores polymorphic `AIContent` plus page numbers, then sends a
`DataContent` query through the configured provider contract.

---

<span class="kicker">Boundary checks</span>

## Evidence stays extraction-only

- exact provider Markdown never becomes canonical `Text`
- confidence, geometry, raw objects, and provider properties remain extraction evidence
- typed reader handoff supports node/evidence lookup while a provider without geometry remains valid
- PdfPig adds reader/page-count metadata and recursive page references
- independent Documents-only consumer depends only on `System.Text.Json`

<div class="output">

```text
Markdown: exact=extraction-only canonical-empty=true
Loss: extraction evidence retained; ingestion/chunks/records isolated
```
</div>

---

<span class="kicker">Exact feed</span>

## Six packages, one implementation commit

`10.8.0-preview2neutral.6f7f3fa`

- DataIngestion + Abstractions
- DataIngestion.DocumentExtraction
- DocumentExtraction + Abstractions
- Documents.Abstractions

Every hash, package ID, version, repository URL, and nuspec commit resolves to
`6f7f3fa75d08599eb5005a0cd3db17d20694e1a8`.

<div class="slide-actions">
<span class="run">scripts/build-local-feed.sh</span>
</div>

---

<span class="kicker">Comparison only</span>

## No winner declared here

- bridge handoff: `a3033e0a`
- evaluated bridge source: `c1913907`
- bridge consumer: `aa55dfe6`
- neutral implementation: `6f7f3fa7`
- neutral presentation: `7e5172fe`

This evidence does not claim merge readiness, OCR quality, performance, live-provider behavior,
archive rebuild identity, or resolved schema evolution.

---

<span class="kicker">Superseded history</span>

## Generic-main evidence is not current

Consumer commit `75bbb4195baf8ed92f702a6cfa1f02ec5360399e` remains reachable for audit.

It is explicitly superseded and is not Preview 2 evidence.

---

<div class="title-slide">

<span class="kicker">Run the evidence</span>

# Verify the feed, then run the proofs

<p class="subtitle"><code>scripts/build-local-feed.sh</code><br><code>scripts/validate-neutral-tree.sh</code></p>

<div class="title-rule"></div>

<p class="byline">DO NOT MERGE until Adam selects an architecture</p>

</div>
