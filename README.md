# One interface for every OCR engine

A grounded talk and runnable sample set showing `IDocumentExtractionClient`, a provider-neutral seam for document
parsing in .NET. Four OCR engines (a vision LLM, Mistral OCR, Azure Document Intelligence, and Azure
Content Understanding) run through one interface; a thin `OcrDocumentReader` bridges that capability
into a real Microsoft.Extensions.DataIngestion (MEDI) pipeline; the page model is carried through
chunking so answers cite their source page; and the PdfPig reader shows the same seam composed a
second way (digital text first, OCR only the pages that need it).

- **Slides:** [`slides.md`](slides.md) (message-first, every claim grounded on a real run)
- **Speaker outline + abstract + primer:** [`talk/`](talk/) — the deck opens with a 90-second
  [primer](talk/primer.md) (what OCR is, the extract→structure→chunk→retrieve→answer vocabulary, and
  the two engine archetypes) so the payoff lands for everyone.
- **Runnable proof:** [`samples/`](samples/) with captured output in `samples/output/` — including
  `09-images-and-uris.cs` (figures + `UriContent`), `10-eval-ocr-vs-pdfpig.cs` (an eval harness
  measuring OCR engines vs naive PdfPig), and `11-vision-structured-output.cs` (structured
  transcription from the vision path).
- **Diagrams:** [`assets/diagrams/`](assets/diagrams/) — four hand-authored branded SVGs (stack,
  data-flow/boundary, composition, eval harness) embedded in the deck and reusable standalone.
- **Reproducible feed:** [`scripts/build-local-feed.sh`](scripts/build-local-feed.sh) packs the real
  `dotnet/extensions` code (preview2 + #7588) into `local-feed/` — the samples run on the real
  types, not a vendored copy.
- **The body of work this drives, across two repos:**
  - dotnet/extensions [#7588](https://github.com/dotnet/extensions/pull/7588) (IDocumentExtractionClient) — the live
    seam. Page provenance rides the shipping API (the reader emits one section per page; the samples
    chunk per page), so an earlier chunk-propagation proposal,
    [#7516](https://github.com/dotnet/extensions/pull/7516), closed unmerged and is no longer needed
  - CommunityToolkit/AI [#13](https://github.com/CommunityToolkit/AI/issues/13) (design),
    [#15](https://github.com/CommunityToolkit/AI/pull/15) (VisionLMOcrClient, closes #13),
    [#14](https://github.com/CommunityToolkit/AI/pull/14) (PdfPig reader composing any IDocumentExtractionClient)

Built on the reveal-presentation-template. The rest of this README is the template's operating
manual: how the deck, themes, layouts, and grounding workflow fit together.

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
`az login`, set your endpoints, run a sample. (To rebuild/refresh the feed from public GitHub refs,
run `scripts/build-local-feed.sh`.)

```bash
az login                                            # keyless DefaultAzureCredential
# set endpoints once via user-secrets (UserSecretsId iocrclient-demo) — see docs/SETUP.md
dotnet run samples/05-one-loop-four-clients.cs                     # defaults to the complex USGS fact sheet
dotnet run samples/05-one-loop-four-clients.cs -- samples/data/survival-kit.pdf  # the simple born-digital baseline
dotnet run samples/06-medi-pipeline.cs              # OcrDocumentReader -> MEDI chunker; per-page provenance
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
