# Samples

Slides should not rest on vibes. Each sample is the smallest proof of one claim in the deck. Run it,
capture the real output to `output/<name>.txt`, and cite it on a slide. Every output here is from a
live run.

The demo shows one interface, `IOcrClient`, in front of four very different OCR engines, then bridges
that capability into a real MEDI pipeline whose page structure makes the final answer citable.

## The real interface (not a vendored copy)

`IOcrClient` is proposed in [dotnet/extensions #7588](https://github.com/dotnet/extensions/pull/7588)
and is not on nuget.org yet. Rather than vendor a copy, `scripts/build-local-feed.sh` packs the REAL
branch — `data-ingestion-preview2` + #7516 (opt-in metadata propagation) + #7588 (IOcrClient) — into
`../local-feed/` at version `10.8.0-dev`. `nuget.config` resolves those `Microsoft.Extensions.*`
packages from the local feed and everything else (Azure SDKs, OpenAI, PdfPig) from nuget.org.

So the samples `using Microsoft.Extensions.AI;` and `using Microsoft.Extensions.DataIngestion;` bind to
the actual types. When #7588 + #7516 ship, delete the local feed and the `<clear/>` in `nuget.config`
and bump to the published versions. `ocr-shape/` holds the four provider implementations
(`VisionLlmOcrClient`, `FoundryMistralOcrClient`, `AzureDocumentIntelligenceClient`,
`ContentUnderstandingClient`) plus `OcrDocumentReader` — the `IOcrClient` -> MEDI bridge.

## The boundary this demo draws

- **`IOcrClient`** (`Microsoft.Extensions.AI`, #7588) — a *capability*: bytes -> `OcrResult`. Provider
  implementations live in `ocr-shape/`.
- **`IngestionDocumentReader`** (MEDI) — a *pipeline stage*: `ReadAsync -> IngestionDocument`.
- **`OcrDocumentReader`** (`ocr-shape/OcrDocumentReader.cs`) — the *bridge*: one reader composing any
  `IOcrClient`, stamping `page_number` / `ocr_source` / `confidence` / bbox with the same keys
  `PdfPigReader` uses.

## Files

| Sample | Needs | What it shows |
| --- | --- | --- |
| `01-vision-ocr.cs` | Azure OpenAI (gpt-4.1-mini) | A vision LLM behind `IOcrClient`. Transcribe-by-seeing: one Markdown blob, no page model. |
| `02-document-intelligence.cs` | Azure Document Intelligence | Document-native engine, `prebuilt-layout`. Structured pages and tables. |
| `03-content-understanding.cs` | Azure Content Understanding (CU region) | A third engine, third wire protocol, same result. |
| `04-mistral-ocr.cs` | Mistral OCR on Azure AI Foundry | Purpose-built document AI: whole PDF in one call, per-page Markdown. |
| `05-one-loop-four-clients.cs` | all four above | The payoff. Four engines, one loop, identical call. |
| `06-medi-pipeline.cs` | Mistral OCR | The bridge: `OcrDocumentReader` -> `SectionChunker`, with #7516 metadata propagation shown before/after ([#7516](https://github.com/dotnet/extensions/pull/7516)). |
| `07-e2e-rag.cs` | Mistral OCR + a chat model | End to end: OCR -> reader -> chunk (page provenance) -> retrieve -> page-cited answer. |
| `08-pdfpig-reader.cs` | Mistral OCR (+ PdfPig from nuget) | The same seam a second way: PdfPig native text + per-page OCR fallback composing `IOcrClient` ([CommunityToolkit #14](https://github.com/CommunityToolkit/AI/pull/14)). |
| `09-images-and-uris.cs` | Mistral OCR + Azure Document Intelligence | The result grows: `OcrPage.Images` (figure bytes + bbox + caption) via `IncludeImages`, and the `UriContent` overload — both proposed into [#7588](https://github.com/dotnet/extensions/pull/7588). Image bytes saved under `output/images/` (gitignored). |
| `10-eval-ocr-vs-pdfpig.cs` | `bench/OcrBench/` + a judge model | Does OCR beat naive PdfPig? Same chunk→retrieve→answer pipeline, one variable (the extractor), scored by a custom deterministic `OcrExtractionEvaluator` + the built-in NLP F1. Writes `bench/report/leaderboard.md`. |
| `11-vision-structured-output.cs` | Azure OpenAI (gpt-4.1-mini) | Opt-in structured transcription from the vision path (`GetResponseAsync<T>` over an OcrResult-shaped schema), plus typed POCO extraction via the inner `IChatClient` (`GetService<IChatClient>()`). Degrades to freeform when unsupported. |
| `14-ollama-glm-ocr.cs` | Ollama + `glm-ocr` (local; no cloud, no `az login`) | A **local, open-weights** OCR engine behind `IOcrClient`. GLM-OCR (~0.9B) reached through OllamaSharp's native `IChatClient` and the repo's existing `VisionLlmOcrClient` — no new provider class. The page image is pulled with PdfPig (image *extraction*, not rasterization); the two glm-ocr prompt/stop quirks and Ollama's missing done-frame are patched by composition **on the Ollama client only** (`ocr-shape` untouched). |
| `15-ocr-engine-comparison.cs` | `bench/OcrBench/` + Azure DI + Mistral OCR + Ollama `glm-ocr` | Cloud vs local through one seam ([BlueGuardrails](https://blueguardrails.com/en/blog/high-throughput-vlm-ocr)'s cost/locality framing): Azure Document Intelligence vs Mistral OCR vs GLM-OCR (local) on the same scanned doc — latency (measured here), structure (tables/figures), and cited $/1k-page cost. An inline `PdfImageOcrClient` feeds the image-only local engine per page; missing cloud creds graceful-skip. |

## Configuration (no secrets in code)

All endpoints come from **`dotnet user-secrets`** (UserSecretsId `iocrclient-demo`) with environment
variables as a fallback, and auth is keyless (`DefaultAzureCredential`). This works for file-based
samples (`dotnet run NN-*.cs`) too — no `.csproj` needed. Run `az login` first; the signed-in identity
needs a Cognitive Services data-plane role on the accounts. Nothing sensitive is committed.

```bash
# set once — stored outside the repo, keyed by --id iocrclient-demo
dotnet user-secrets set "OCR:FoundryEndpoint"              "https://<your-foundry-account>.services.ai.azure.com" --id iocrclient-demo
dotnet user-secrets set "OCR:DocIntelEndpoint"             "https://<your-account>.cognitiveservices.azure.com"   --id iocrclient-demo
dotnet user-secrets set "OCR:OpenAIEndpoint"               "https://<your-account>.openai.azure.com"              --id iocrclient-demo
dotnet user-secrets set "OCR:ContentUnderstandingEndpoint" "https://<your-cu-account>.services.ai.azure.com"      --id iocrclient-demo
dotnet user-secrets set "OCR:VisionDeployment"             "gpt-4.1-mini"          --id iocrclient-demo
dotnet user-secrets set "OCR:EmbedDeployment"              "text-embedding-3-small" --id iocrclient-demo
dotnet user-secrets set "OCR:MistralModel"                 "mistral-ocr-4-0"       --id iocrclient-demo
```

The **local** engine (samples `14`/`15`, GLM-OCR on Ollama) needs **no secrets and no `az login`** — just
`ollama pull glm-ocr`. It defaults to `http://localhost:11434` and model `glm-ocr`; override with the
optional `OCR:OllamaEndpoint` / `OCR:OllamaOcrModel` keys (or env vars) if your Ollama runs elsewhere.

## Run

Every sample **defaults to the complex USGS fact sheet** (`data/usgs-petroleum-assessment.pdf`) —
tables, raster figures, two-column layout — the document where OCR earns its keep. Pass
`-- data/survival-kit.pdf` to any of them to see the **simple born-digital baseline** it graduates from.

```bash
../scripts/build-local-feed.sh            # once: pack the real bits into ../local-feed
az login

# one engine at a time (USGS default)
dotnet run 04-mistral-ocr.cs
dotnet run 02-document-intelligence.cs

# the payoff, the pipeline, the closer, the sibling reader
dotnet run 05-one-loop-four-clients.cs
dotnet run 06-medi-pipeline.cs
dotnet run 07-e2e-rag.cs                  # USGS default + its oil-estimate question
dotnet run 08-pdfpig-reader.cs

# the simple baseline you graduate from
dotnet run 05-one-loop-four-clients.cs -- data/survival-kit.pdf

# round 2/3: figures + UriContent, the eval (born-digital + scanned twins), structured output
dotnet run 09-images-and-uris.cs
dotnet run 10-eval-ocr-vs-pdfpig.cs       # runs the USGS born-digital + scanned twins
dotnet run 11-vision-structured-output.cs

# round 4: a LOCAL, open-weights engine (GLM-OCR on Ollama) behind the same seam — no cloud, ~$0/page
ollama pull glm-ocr                       # the entire setup for sample 14; no secrets, no az login
dotnet run 14-ollama-glm-ocr.cs           # 100% local, GPU-backed
dotnet run 15-ocr-engine-comparison.cs    # cloud vs local: Azure DI vs Mistral OCR vs GLM-OCR (needs az login for the two cloud engines)
```

## Capture for a slide

```bash
dotnet run 05-one-loop-four-clients.cs > output/05-one-loop-four-clients.txt   # USGS default
```

Model output is not deterministic, so capture one real run and note on the slide that it is one run.
Never paste output you did not run. If you cannot reach a provider, leave a `TODO run against
<provider>` line rather than inventing a result.

## Add a sample

1. Create `samples/<name>.cs` with the smallest code that checks one claim.
2. Run it and save `output/<name>.txt`.
3. Cite the command and the captured output on a slide (see the `code-output` layout).
4. Re-run after any code, prompt, model, SDK, or package change.
