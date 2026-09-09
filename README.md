# DO NOT MERGE: explicit bridge consumer validation

This draft is the Preview 2 consumer-evidence companion to
[`luisquintanilla/extensions` PR #1](https://github.com/luisquintanilla/extensions/pull/1) at
presentation head `a1eb56c4c497738ef06c557f63cdc071082a4536`. Packages are pinned to the
evaluated implementation commit
[`c1913907f05148370a84824b669d73249bb502e4`](https://github.com/luisquintanilla/extensions/commit/c1913907f05148370a84824b669d73249bb502e4),
not the later presentation-only head.

```csharp
using IDocumentExtractionClient extractionClient = new FixtureDocumentExtractionClient();
var reader = new DocumentExtractionReader(
    extractionClient,
    new() { MarkdownOnlyPagePolicy = MarkdownOnlyPagePolicy.PreserveAsMarkdown });
IngestionDocument document = await reader.ReadAsync(
    new MemoryStream([0x25, 0x50, 0x44, 0x46]),
    "quarterly-review.pdf",
    "application/pdf");

IngestionChunker chunker = new SectionChunker(new(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 256,
    OverlapTokens = 0,
});
IAsyncEnumerable<IngestionChunk> chunks = chunker.ProcessAsync(document);
VectorStoreCollection<Guid, Preview2ChunkRecord> records =
    store.GetIngestionRecordCollection<Preview2ChunkRecord>("chunks", dimensions);
using var writer = new VectorStoreWriter<Preview2ChunkRecord>(records);
await writer.WriteAsync(chunks);
```

Run the credential-free proof with `dotnet run samples/17-explicit-bridge-validation.cs`. The
[sample](samples/17-explicit-bridge-validation.cs) uses a realistic two-page fake result with a
heading, text, structured table, image, provider-specific block kind, geometry, confidence, raw
objects, and canonical-plus-Markdown input. A separate deterministic subcase proves the default
Markdown-only failure and explicit preservation policy. Its
[captured output](samples/output/17-explicit-bridge-validation.txt)
comes from an actual run against the six hash-verified packages at
`10.8.0-preview2bridge.c191390`.

**What this proves:** the built-in bridge maps canonical structure into Preview 2 MEDI; non-generic
`IngestionChunk` carries polymorphic `AIContent`, required `TokenCount`, and pages; the stock typed
writer persists `TextContent` and `DataContent`; and configured VectorData embedding runs during
upsert and retrieval. **What it does not prove:** merge readiness, production performance, provider
quality, reproducible package archives across arbitrary environments, or exhaustive live-provider
behavior. Sample
[`06-medi-pipeline.cs`](samples/06-medi-pipeline.cs) keeps the optional Mistral-backed path, but it
requires existing local user-secrets and Azure login and was not needed for the deterministic proof.
All four provider implementations are compile-only evidence for this pinned head. None was executed
against a live provider during this comparison validation.
The current structured Vision and Content Understanding demos can emit partial canonical elements
alongside provider Markdown. Because the explicit bridge intentionally gives canonical elements
precedence, those modes remain direct-extraction demos rather than validated bridge paths. The hero
uses its Markdown-only Vision mode, and sample 06 leaves image extraction disabled.

## Existing talk and provider samples

The repository remains a grounded talk and runnable sample set showing `IDocumentExtractionClient`
across four OCR engines. This draft only compares the explicit bridge architecture and must not merge
until Adam selects an architecture. The existing slide deck describes the earlier app-owned bridge
and is historical context, not evidence for this comparison draft.

- **Slides:** [`slides.md`](slides.md) (message-first, every claim grounded on a real run)
- **Speaker outline + abstract + primer:** [`talk/`](talk/) — the deck opens with a 90-second
  [primer](talk/primer.md) (what OCR is, the extract→structure→chunk→retrieve→answer vocabulary, and
  the two engine archetypes) so the payoff lands for everyone.
- **Runnable proof:** [`samples/`](samples/). The current comparison evidence is
  [`17-explicit-bridge-validation.cs`](samples/17-explicit-bridge-validation.cs) with its executed
  capture in [`samples/output/`](samples/output/). Provider samples remain available for credentialed
  runs, but their earlier captures were removed after the API change.
- **Diagrams:** [`assets/diagrams/`](assets/diagrams/) — four hand-authored branded SVGs (stack,
  data-flow/boundary, composition, eval harness) embedded in the deck and reusable standalone.
- **Pinned feed:** [`scripts/build-local-feed.sh`](scripts/build-local-feed.sh) validates, installs,
  or explicitly rebuilds exactly six packages from `c1913907f05148370a84824b669d73249bb502e4`, then verifies package
  SHA-256, ID, version, repository, and commit. Consumer restore caches are repo-local.
- **The body of work this drives, across two repos:**
  - [`luisquintanilla/extensions` PR #1](https://github.com/luisquintanilla/extensions/pull/1) is
    the explicit bridge architecture under comparison.
  - CommunityToolkit/AI [#13](https://github.com/CommunityToolkit/AI/issues/13) (design),
    [#15](https://github.com/CommunityToolkit/AI/pull/15) (VisionLMOcrClient, closes #13),
    [#14](https://github.com/CommunityToolkit/AI/pull/14) (PdfPig reader composing any IDocumentExtractionClient)

Built on the reveal-presentation-template. The rest of this README is the template's operating
manual: how the deck, themes, layouts, and grounding workflow fit together.

## Superseded consumer history

Heads `604b6ee950074bf3ddb8fba8500bb974f6c744cf` and earlier validated the historical
generic-main experiment. They are retained only as immutable history and are not Preview 2 evidence.

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

The samples run on the exact Preview 2 bridge `IDocumentExtractionClient` + MEDI bits. Those APIs
aren't on nuget.org yet, so this
repo commits an unofficial, hash-pinned [`local-feed/`](local-feed/README.md) from the exact source.
Run `scripts/build-local-feed.sh` to validate it before restoring or running samples.

```bash
scripts/build-local-feed.sh
az login                                            # keyless DefaultAzureCredential
# set endpoints once via user-secrets (UserSecretsId iocrclient-demo) — see docs/SETUP.md
dotnet run samples/05-one-loop-four-clients.cs                     # defaults to the complex USGS fact sheet
dotnet run samples/05-one-loop-four-clients.cs -- samples/data/survival-kit.pdf  # the simple born-digital baseline
dotnet run samples/17-explicit-bridge-validation.cs # deterministic built-in bridge proof, no credentials
dotnet run samples/06-medi-pipeline.cs              # optional live Mistral -> built-in bridge path
dotnet run samples/07-e2e-rag.cs                    # USGS default + its oil-estimate question
dotnet run samples/08-pdfpig-reader.cs              # native text + OCR fallback on USGS
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
