# Setup & prerequisites

This is a **demo / reference repo** — a runnable companion to the "one interface for every OCR
engine" talk. It is not an official Microsoft project (see [`local-feed/README.md`](../local-feed/README.md)
for what the pinned packages are). The goal here is to get you running the samples so you can react
to the proposed `IDocumentExtractionClient` shape.

Native PdfPig needs no service credentials. Cloud extraction is optional; pick the providers you
want to try. Full live E2E additionally requires real embedding and chat deployments.

## 1. Install the .NET SDK

Install a .NET 10 SDK (the samples are C# file-based apps run with `dotnet run *.cs`).

- Official install guide: <https://learn.microsoft.com/dotnet/core/install/>

Verify:

```bash
dotnet --version
```

## 2. Get the code and the packages

Import the frozen `10.10.0-dev.e2e.cf60fa88.1` integration manifest with
`scripts/Import-E2ECandidate.ps1`. Its 16 runtime packages go into ignored `validation-feed/`.
Exact source pins and commands are in [`samples/README.md`](../samples/README.md).
The old committed `local-feed/` is historical, not the migrated stack.

The standalone `nuget.config` prevents inherited source mappings from redirecting candidate
packages. It uses the existing public dotnet mirror for public dependencies; nuget.org is retained
but disabled because this environment fails its TLS handshake. Enable it and map public packages
there on a host that can reach it. No dependency feed errors are ignored.

For source repacking, use `Build-E2EFeed.ps1` with the frozen source manifest, already-reconciled
worktree and a new candidate version. Never fetch public main as a substitute or rebuild an
immutable version from changed source.

## 3. Authentication (keyless)

Every sample authenticates with **`Azure.Identity.DefaultAzureCredential`** — no keys in code or
config. Sign in once, with an identity that has a **Cognitive Services** data-plane role (e.g.
*Cognitive Services User*) on the resources you use:

```bash
az login
```

- `DefaultAzureCredential`: <https://learn.microsoft.com/dotnet/api/azure.identity.defaultazurecredential>
- Azure RBAC for Azure AI services: <https://learn.microsoft.com/azure/ai-services/authentication>

## 4. Configure existing services

No validation command provisions resources. Endpoints and deployment/model names are read from **`dotnet user-secrets`** (UserSecretsId
`iocrclient-demo`), with environment variables as a fallback. This works for the file-based samples
too — no `.csproj` needed.

- `dotnet user-secrets`: <https://learn.microsoft.com/aspnet/core/security/app-secrets>

| Engine | Samples | Create the resource (official docs) | user-secret key(s) |
| --- | --- | --- | --- |
| **Azure OpenAI** vision (gpt-4o / gpt-4.1-mini) | `01`, `05`, `11`, and embeddings for `07`/hero | [Deploy a vision-enabled chat model](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/gpt-with-vision) | `OCR:OpenAIEndpoint`, `OCR:VisionDeployment`, `OCR:EmbedDeployment` |
| **Azure AI Document Intelligence** | `02`, `05`, `09` | [Create a Document Intelligence resource](https://learn.microsoft.com/azure/ai-services/document-intelligence/) | `OCR:DocIntelEndpoint` |
| **Azure AI Content Understanding** | `03`, `05` | [Content Understanding overview / quickstart](https://learn.microsoft.com/azure/ai-services/content-understanding/overview) | `OCR:ContentUnderstandingEndpoint` |
| **Mistral OCR on Azure AI Foundry** | `04`, `05`, `06`, `07`, `08`, `09` | [Azure AI Foundry model catalog](https://learn.microsoft.com/azure/ai-foundry/) (deploy a Mistral OCR model) | `OCR:FoundryEndpoint`, `OCR:MistralModel` |

Set what you need (example values — use your own):

```bash
dotnet user-secrets set "OCR:OpenAIEndpoint"               "https://<your-account>.openai.azure.com"            --id iocrclient-demo
dotnet user-secrets set "OCR:VisionDeployment"             "gpt-4.1-mini"                                       --id iocrclient-demo
dotnet user-secrets set "OCR:EmbedDeployment"              "text-embedding-3-small"                             --id iocrclient-demo
dotnet user-secrets set "OCR:DocIntelEndpoint"             "https://<your-account>.cognitiveservices.azure.com" --id iocrclient-demo
dotnet user-secrets set "OCR:ContentUnderstandingEndpoint" "https://<your-cu-account>.services.ai.azure.com"    --id iocrclient-demo
dotnet user-secrets set "OCR:FoundryEndpoint"              "https://<your-foundry-account>.services.ai.azure.com" --id iocrclient-demo
dotnet user-secrets set "OCR:MistralModel"                 "mistral-ocr-4-0"                                    --id iocrclient-demo
```

Prefer process-local environment variables? .NET hierarchical configuration uses
`OCR__OpenAIEndpoint`, `OCR__VisionDeployment`, `OCR__EmbedDeployment`,
`OCR__DocIntelEndpoint`, `OCR__ContentUnderstandingEndpoint`, `OCR__FoundryEndpoint`,
and `OCR__MistralModel`. The double underscore maps to `:`; legacy single-underscore
names from older grounding scripts are not aliases in `DemoConfig`.

For local keyless validation, `DefaultAzureCredential` can use the existing Azure CLI
sign-in. With the current Azure SDK dependency, process-local
`AZURE_TOKEN_CREDENTIALS=AzureCliCredential` selects that source without copying a
token or retrieving an account key. Do not enable verbose credential/HTTP body logging.
On this host, the excessively long inherited `PATH` prevented SDK CLI authentication
even though `az account show` worked. A short process-local `PATH` containing Windows
System32, Windows PowerShell, the Azure CLI `wbin`, and dotnet fixes SDK authentication.
This is a validation-process setting, not a global machine environment change.
In this resolved graph, the SDK type `Azure.Identity.DefaultAzureCredential` is
defined in `Azure.Core` 1.53.0; a separate `Azure.Identity.dll` is not expected.

An advertised Content Understanding endpoint alone is not a successful capability check:
one tested existing resource returned route-level 404, while another returned its analyzer
listing and completed analysis successfully. Validate the actual data-plane route before
selecting the resource; do not provision or reconfigure analyzers just to hide a failed check.
The final live provider matrix uses existing active endpoints and deployments only.

For a synthetic-only hero inspection instance, set process-local
`Ingestion__Directory` to an isolated folder containing only the generated fixture
and `VectorStore__Path` to a copy of the validation database. The default ingestion
folder remains `wwwroot/Data`. Keep the original validation artifacts untouched.
The source inspector also needs the matching fixture under `wwwroot/Data` to verify
its hash; never put private documents there for a shared inspection instance.

## 5. Run

```bash
dotnet run samples/05-one-loop-four-clients.cs      # native PdfPig; add --allow-live for configured cloud providers
```

See [`samples/README.md`](../samples/README.md) for the full sample table and what each one proves.

## Optional local layout

RT-DETR is opt-in layout detection on actual PDFium-rendered page pixels. Native PdfPig remains
the default and no new local OCR recognizer is installed. The pinned compatible export is
`xberg-io/layout-models`, revision `c6bf493e2f7b0b9a29a5870da9880c14e20ff0a3`, path
`rtdetr/model.onnx`. The publisher declares Apache-2.0 in model-card/API metadata; no standalone
LICENSE file was found in its sibling listing. Treat that as declared metadata, not completed
redistribution review. Do not commit or publish downloaded weights.

```powershell
$model = '<your-local-model-directory>\rtdetr-model.onnx'
if (Test-Path -LiteralPath $model) { throw 'Do not overwrite an existing model.' }
$url = 'https://huggingface.co/xberg-io/layout-models/resolve/c6bf493e2f7b0b9a29a5870da9880c14e20ff0a3/rtdetr/model.onnx'
Invoke-WebRequest -Uri $url -OutFile $model
$expected = '3bf2fb0ee6df87435b7ae47f0f3930ec3dc97ec56fd824acc6d57bc7a6b89ef2'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $model).Hash.ToLowerInvariant() -ne $expected) {
    Remove-Item -LiteralPath $model
    throw 'RT-DETR model fingerprint mismatch.'
}
dotnet run --project .\samples\proofs\FullE2EProof.csproj -- `
  --mode offline --provider pdfpig --layout rtdetr --model $model --model-sha256 $expected
```

The runtime is CPU ONNX Runtime with Docnet.Core 2.6.0/PDFium and SkiaSharp 3.119.0.
The model expects its specific float32 RGB 640x640 export and 17-label map. Its
`orig_target_sizes` tensor must contain `[width,height]`: the pinned postprocessor
multiplies that pair into `[x0,y0,x1,y1]`. Reversing the axes displaces boxes on
non-square pages; the real-model regression and persisted E2E overlap oracle detect it.
Arbitrary RT-DETR exports are not interchangeable. Missing models, wrong hashes and unsupported rotated/cropped
transforms fail explicitly. Detection confidence is not OCR confidence or proof of text recognition.

For the hero, set `OCR:Provider=pdfpig`, `OCR:PdfPigLayout=rtdetr`,
`OCR:LayoutModelPath` and `OCR:LayoutModelSha256`. Optional
`OCR:LayoutConfidenceThreshold` defaults to 0.5. Embedding/chat service configuration is
independent of this local extraction choice.

## Feedback wanted

This repo exists to gather feedback on the **proposed** building blocks while they're still in
review. If you have thoughts on the shape, that's the point:

- **`IDocumentExtractionClient`** — the provider-neutral page-observation seam
- **`DocumentExtractionReader`** — direct shared Documents ingestion into MEDI

Open an issue here for demo/repro problems, or comment on the PRs for API-shape feedback.
