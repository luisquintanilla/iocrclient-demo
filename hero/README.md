# Hero OCR RAG app

This is the capstone app for the OCR -> MEDI ingestion pipeline -> RAG flow. It is scaffolded from the .NET `aichatweb` Aspire template, then swapped so PDFs go through the shared `IDocumentExtractionClient` shape before they are chunked and written to a local vector store.

## Secrets

The app reads the same user-secrets id as the samples: `iocrclient-demo`.

```bash
dotnet user-secrets set "OCR:OpenAIEndpoint" "https://<your-aoai-resource>.openai.azure.com/" --id iocrclient-demo
dotnet user-secrets set "OCR:VisionDeployment" "<chat-or-vision-deployment>" --id iocrclient-demo
dotnet user-secrets set "OCR:EmbedDeployment" "<embedding-deployment>" --id iocrclient-demo
```

Authentication is keyless through `Azure.Identity.DefaultAzureCredential`, so run `az login` with an identity that can access the Azure OpenAI resource.

## Run

```bash
cd hero
dotnet run --project hero.AppHost
```

If the Aspire CLI is installed, this also works:

```bash
cd hero
aspire run
```

Open the Aspire dashboard from the AppHost output. The template `ServiceDefaults` are intact and subscribe to `Experimental.Microsoft.Extensions.AI` and `Experimental.Microsoft.Extensions.DataIngestion`, so chat, OCR, chunking, embedding, and retrieval activity can show up as OpenTelemetry traces.

## Vector store shape

The intended production shape is Qdrant as an Aspire-managed container. This environment has no Docker/container runtime, so the hero uses `CommunityToolkit.VectorData.SqliteVec` (the maintained MEVD connector — the `Microsoft.SemanticKernel.Connectors.*` line is being deprecated) and writes `vector-store.db` locally.

## Ingestion path

`hero.Web` references `../samples/ocr-shape/OcrShape.csproj` and uses:

```text
VisionLlmOcrClient (IDocumentExtractionClient)
  -> DocumentExtractionReader
  -> IngestionPipeline
  -> VectorStoreWriter<IngestedChunk>
  -> SqliteVec
```

PDFs in `hero.Web/wwwroot/Data` use OCR. Markdown files use a small local reader so the template's sample markdown still indexes without Docker.

The ingested set features the complex **USGS Timan-Pechora petroleum-assessment fact sheet** (born-digital and a scanned image-only twin) — tables, raster figures, two-column layout, and a no-text-layer scan — so the OCR → pipeline → answer path (and its OpenTelemetry traces) runs on the document where OCR earns its keep. The simple `Example_Emergency_Survival_Kit.pdf` stays as the born-digital baseline that "just works."

This repo does not contain Azure endpoints, keys, or accounts. End-to-end execution requires your local user-secrets and live Azure OpenAI access.
