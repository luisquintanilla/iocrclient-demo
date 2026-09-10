# Setup & prerequisites

This is a **demo / reference repo** — a runnable companion to the "one interface for every OCR
engine" talk. It is not an official Microsoft project (see [`local-feed/README.md`](../local-feed/README.md)
for what the pinned packages are). The goal here is to get you running the samples so you can react
to the proposed `IDocumentExtractionClient` shape.

You do **not** need all four OCR engines. Pick the one(s) you want to try — every sample lists what
it needs, and the combined loop (`05`) simply skips engines you haven't configured.

## 1. Install the .NET SDK

Install a current .NET SDK (the samples are C# file-based apps run with `dotnet run *.cs`).

- Official install guide: <https://learn.microsoft.com/dotnet/core/install/>

Verify:

```bash
dotnet --version
```

## 2. Get the code and the packages

The six Preview 2 neutral architecture packages are **not on nuget.org**. The repo ships the exact
hash-verified implementation feed in [`local-feed/`](../local-feed/README.md). Run
`scripts/build-local-feed.sh` to verify package count, IDs, hashes, version, repository, and commit.
Published MEAI and provider dependencies still resolve from NuGet.org.

## 3. Authentication (keyless)

Credential-backed Azure samples authenticate with **`Azure.Identity.DefaultAzureCredential`**. No
keys are stored in code or config. Sign in once with an identity that has a **Cognitive Services** data-plane role (e.g.
*Cognitive Services User*) on the resources you use:

```bash
az login
```

- `DefaultAzureCredential`: <https://learn.microsoft.com/dotnet/api/azure.identity.defaultazurecredential>
- Azure RBAC for Azure AI services: <https://learn.microsoft.com/azure/ai-services/authentication>

## 4. Provision the engine(s) you want, then store endpoints

Endpoints and deployment/model names are read from **`dotnet user-secrets`** (UserSecretsId
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

Prefer environment variables? The same values are read from `OCR_OPENAI_ENDPOINT`,
`OCR_VISION_DEPLOYMENT`, `OCR_DI_ENDPOINT`, `OCR_CU_ENDPOINT`, `OCR_FOUNDRY_ENDPOINT`, and
`OCR_MISTRAL_MODEL` (see the untracked `samples/.env.grounding` template).

## 5. Run

```bash
dotnet run samples/05-one-loop-four-clients.cs      # four engines, one loop (skips any you didn't configure)
```

See [`samples/README.md`](../samples/README.md) for the full sample table and what each one proves.

## Feedback wanted

This repo exists to gather feedback on the **proposed** building blocks while they're still in
review. If you have thoughts on the shape, that's the point:

- **neutral implementation:** `704a3e44ef4d7b053748780549fc2c8e929a444b`
- **presentation evidence only:** `7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2`

Open an issue here for demo/repro problems, or comment on the PRs for API-shape feedback.
