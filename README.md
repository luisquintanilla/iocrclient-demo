# Preview 2 neutral shared-tree consumer validation

> **DRAFT COMPARISON. DO NOT MERGE until Adam selects an architecture.**

This is the executable consumer companion for the neutral architecture on the authoritative
`data-ingestion-preview2` line:

- evaluated implementation and package source:
  [`704a3e44ef4d7b053748780549fc2c8e929a444b`](https://github.com/luisquintanilla/extensions/commit/704a3e44ef4d7b053748780549fc2c8e929a444b)
- evidence-only presentation:
  [`7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2`](https://github.com/luisquintanilla/extensions/commit/7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2)
- common base: `f6ba2df16275bfc5eaf50aeb9327e2ec34ee8129`
- Preview 2 ancestor: `e124c123afeeda2f271f3b99a70eb3cfe187a471`

Only `704a3e44…` supplies packages. The presentation commit is never a package source.

```csharp
using IDocumentExtractionClient client = new FixtureExtractionClient(result);
var reader = new DocumentExtractionReader(client);
var chunker = new SectionChunker(new(tokenizer)
{
    MaxTokensPerChunk = 256,
    OverlapTokens = 0,
});
using var writer = new VectorStoreWriter<Preview2ChunkRecord>(collection);
using var pipeline = new IngestionPipeline(reader, chunker, writer);
```

Preview 2 remains non-generic at the pipeline boundary: `IngestionPipeline`, `IngestionChunker`,
`IngestionChunkProcessor`, `IngestionChunkWriter`, and `IngestionChunk`. Chunk `Content` is
`AIContent`, and every chunk has a required positive `TokenCount`. The stock
`VectorStoreWriter<TRecord>` persists through a typed `IngestionChunkVectorRecord`.

Run the credential-free validation:

```bash
scripts/build-local-feed.sh
scripts/validate-neutral-tree.sh
```

The exact proof is
[`samples/17-neutral-shared-tree-validation.cs`](samples/17-neutral-shared-tree-validation.cs);
its executed output is
[`samples/output/17-neutral-shared-tree-validation.txt`](samples/output/17-neutral-shared-tree-validation.txt).
The independent shared-package proof is
[`samples/documents-only/Program.cs`](samples/documents-only/Program.cs), with captured output in
[`samples/output/documents-only.txt`](samples/output/documents-only.txt).

The six-package feed is closed and hash-verified by ID, version, repository URL, and exact commit.
Published MEAI, provider SDK, vector store, and evaluation dependencies come from NuGet.org.

**This proves:** shared `Document` identity pass-through; exact non-generic chunk content, context,
token counts, source IDs, and pages; typed stock-writer persistence; real provider embeddings during
upsert and query; exact revenue/retention retrieval; mixed `TextContent`/`DataContent`; serialization
round trip; Markdown isolation; PdfPig metadata/provenance; and independent Documents consumption.

**It does not prove:** merge readiness, OCR quality, performance, live-provider behavior, archive
rebuild identity, or settled schema evolution and immutable rewrite policy. `SourceNodeIds` do not
persist by default. Existing collections may need migration for page-number storage. Cross-page
logical hierarchy remains an open decision, and sparse table overlap validation remains
`O(cells^2)`.

Provider-backed samples and the hero compile; providers are not executed and no provider output is
claimed.

## Comparison links

- bridge architecture handoff:
  [`a3033e0aa1aa25e4b5e360d73d500101e6b9af71`](https://github.com/luisquintanilla/extensions/commit/a3033e0aa1aa25e4b5e360d73d500101e6b9af71)
- evaluated bridge source:
  [`c1913907f05148370a84824b669d73249bb502e4`](https://github.com/luisquintanilla/extensions/commit/c1913907f05148370a84824b669d73249bb502e4)
- bridge consumer:
  [`aa55dfe6a6d297b7a3edaf9107007cefcc9f09f6`](https://github.com/luisquintanilla/iocrclient-demo/commit/aa55dfe6a6d297b7a3edaf9107007cefcc9f09f6)

These links support comparison only. This PR does not state a winner.

## Superseded historical generic-main evidence

The previous head
[`75bbb4195baf8ed92f702a6cfa1f02ec5360399e`](https://github.com/luisquintanilla/iocrclient-demo/commit/75bbb4195baf8ed92f702a6cfa1f02ec5360399e)
tested a historical generic-main neutral experiment. It is superseded and is not Preview 2 evidence.
Its commits remain reachable for audit, but no result from that package set is presented as current.

---

# Reveal presentation template

Great talks need a clean deck and proof the audience can trust. This template gives you a RevealJS
deck, a reusable visual system with swappable themes, a layout catalog, and a sample-output
workflow that grounds technical claims on real runs.

It is built to be picked up and extended by **people and AI coding assistants alike** (Copilot CLI,
Claude Code, Cursor, and similar). The same procedures work either way: an assistant runs the
steps, a person runs the plain-checklist fallback. The deck you are looking at is also a live tour
of the template.

## Start here

1. Click **Use this template** on GitHub, or clone this repo.
2. Install and preview:

   ```bash
   npm install
   npm run preview
   ```

   Open http://localhost:8000. Press `S` for speaker view.
3. Read [`AGENTS.md`](AGENTS.md). It is the operating manual for the whole template, for people and
   assistants. It points at the skill for each thing you might want to do.
4. Replace the slides with your talk and ground every claim with a real run.

## What's in here

| Path | What it is |
| --- | --- |
| `slides.md` | The deck. Markdown is the source of truth. Also the live tour. |
| `index.html` | RevealJS bootstrap. Loads base + components + one theme. |
| `css/base.css` | Structural styling. Theme-neutral; reads tokens. |
| `css/components.css` | Reusable slide components. |
| `themes/` | Swappable themes. A theme is one token file. `default.css` + `light.css`. |
| `layouts/` | Canonical copy-paste slide snippets, one per file. |
| `samples/` | Small runnable proofs and captured output. |
| `assets/` | Self-hosted fonts, images, diagrams. |
| `.devcontainer/` | Node + .NET + Azure CLI environment. |
| `.github/skills/` | The extension procedures (start a talk, ground a claim, add a theme or layout, voice-check, rehearse). |
| `.github/workflows/deploy-pages.yml` | GitHub Pages deploy. |

## Extend it

Everything you might add has one home and one procedure, usable by hand or with an assistant:

- **Start a new talk** with the `scaffold-new-talk` skill.
- **Add a theme** by copying `themes/default.css` and changing the tokens (`new-theme` skill).
- **Add a layout or component** from `layouts/README.md` and `css/components.css` (`new-layout` skill).
- **Ground a claim** with a sample in `samples/` (`slide-ground` skill).
- **Check the voice** of your prose (`voice-check` skill).
- **Time the talk** before delivery (`rehearse-timing` skill).

See [`AGENTS.md`](AGENTS.md) for the full map and conventions.

## Run the samples

**Prerequisites:** the .NET SDK, `az login`, and the Azure resource(s) for whichever engine(s) you
want to try. See **[`docs/SETUP.md`](docs/SETUP.md)** for a per-engine walkthrough (with official
Microsoft Learn links) and the `dotnet user-secrets` keys each one needs.

The samples run on the real `IDocumentExtractionClient` + MEDI bits. Those APIs aren't on nuget.org yet, so this
repo **ships them prebuilt in [`local-feed/`](local-feed/README.md)** — *unofficial* local dev
builds; read that NOTICE — and `nuget.config` resolves them from there. Nothing to build first:
`az login`, set your endpoints, run a sample. Use `scripts/build-local-feed.sh` to verify the
committed feed, or set `SOURCE_FEED` to validate and install a prebuilt replacement with rollback.

```bash
az login                                            # keyless DefaultAzureCredential
# set endpoints once via user-secrets (UserSecretsId iocrclient-demo) — see docs/SETUP.md
dotnet run samples/05-one-loop-four-clients.cs                     # defaults to the complex USGS fact sheet
dotnet run samples/05-one-loop-four-clients.cs -- samples/data/survival-kit.pdf  # the simple born-digital baseline
dotnet run samples/06-medi-pipeline.cs              # built-in reader -> MEDI chunker; typed provenance
dotnet run samples/07-e2e-rag.cs                    # USGS default + its oil-estimate question
dotnet run samples/08-pdfpig-reader.cs              # native text or optional whole-document OCR
dotnet run samples/09-images-and-uris.cs            # figures + UriContent overload
(cd samples && dotnet run 10-eval-ocr-vs-pdfpig.cs)                # eval: OCR vs naive PdfPig on the USGS twins
dotnet run samples/11-vision-structured-output.cs   # structured transcription
```

## Ground every technical claim

Use this loop:

1. Write the claim in one sentence.
2. Write the smallest sample that can check it.
3. Run it from a clean shell.
4. Save the exact output in `samples/output/`.
5. Paste the exact output into the slide.
6. Keep the run command visible on the slide or in speaker notes.

Do not paste expected output. If the output is messy, the sample or claim needs work.

## Deploy with GitHub Pages

The workflow builds the deck and publishes `public/` on every push to `main`.

1. Open **Settings → Pages**.
2. Set **Source** to **GitHub Actions**.
3. Push to `main`.
4. Read the Pages URL from the completed workflow run.

The workflow has no hardcoded repository name.

## Credits

Built with RevealJS. Fonts are Space Grotesk and Open Sans under the SIL Open Font License.
