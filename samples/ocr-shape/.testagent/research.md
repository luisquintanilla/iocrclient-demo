# Bounded OCR Shape Test Research

## Scope and constraints

- Root: `samples/ocr-shape`
- Strategy: broad, sequential Research -> Plan -> Implement.
- Production targets are limited to:
  - `Providers/ProviderAdapterHelpers.cs`
  - `Providers/AzureDocumentIntelligenceClient.cs`
  - `Providers/ContentUnderstandingClient.cs`
  - `Providers/FoundryMistralOcrClient.cs`
  - `Providers/VisionLlmOcrClient.cs`
  - `Providers/PdfPigDocumentExtractionClient.cs`
  - `Providers/PdfPageRasterizer.cs`
  - `Providers/RtDetrLayoutDetector.cs`
  - `PdfPigReader.cs`
  - `IngestionChunkExtensions.cs`
- No network, cloud, credentials, ports, timing assertions, or edits outside this root.
- Preserve production behavior. Prefer public APIs and reflection over production changes.
- SDK-style `net10.0` project. The bounded root has no existing test project or test files.
- Offline cache contains `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3, and
  `xunit.runner.visualstudio` 3.1.5. The test project will reference `OcrShape.csproj`,
  thereby using its exact restored `10.10.0-dev.e2e.cf60fa88.1` Documents,
  DocumentExtraction, AI, and DataIngestion dependency family.

## Final replay and geometry additions

- Azure Document Intelligence and Content Understanding both expose public
  client options with `HttpPipelineTransport`, so completed LRO behavior can be
  exercised through real SDK clients without constructing sealed operations or
  using live credentials.
- The replay acceptance requires a realistic initial `202` with
  `operation-location`, a completed `GET` JSON response, two mapped pages
  including a blank page, and both retained and disabled raw-output states.
- Vision structured output must fall back exactly once only when the provider
  rejects schema/response-format capability. It must log a standard warning and
  record fallback provenance. Malformed output and operational failures remain
  terminal.
- The pinned ONNX graph tiles `orig_target_sizes` directly into the
  `[x0,y0,x1,y1]` postprocessor scale. Docling's ONNX runtime source supplies
  `[image.width,image.height]`; `[height,width]` swaps the axes on non-square
  pages. Real rendered inference must overlap a known PdfPig text region, not
  merely return detections.
- Vision free-form requests cannot send a PDF as though Chat Completions
  natively understood it. The adapter must enumerate the actual PdfPig pages,
  render each with PDFium, send one PNG per page to the existing `IChatClient`,
  and emit every original page even when the model response is empty.

## Static source-to-test pairing (Roslyn, executed exactly once)

Command:

```powershell
dotnet run "<installed-skill-root>\find-untested-sources\scripts\Find-UntestedSources.cs" -- ".\samples\ocr-shape"
```

Result: exit code 0 in 356 ms; 12 source files, 0 test files, 12 unpaired files,
0 paired files. All ten bounded targets were reported unpaired; the additional
out-of-scope files were `DemoConfig.cs` and `OcrShapeExtensions.cs`.
No suggested test path was available because no test project existed.

Bounded analyzer inventory (declaration counts):

| Source | Declarations | Static pairing |
|---|---:|---|
| `Providers/PdfPageRasterizer.cs` | 5 | unpaired |
| `Providers/VisionLlmOcrClient.cs` | 5 | unpaired |
| `PdfPigReader.cs` | 3 | unpaired |
| `Providers/PdfPigDocumentExtractionClient.cs` | 3 | unpaired |
| `IngestionChunkExtensions.cs` | 2 | unpaired |
| `Providers/AzureDocumentIntelligenceClient.cs` | 2 | unpaired |
| `Providers/ContentUnderstandingClient.cs` | 2 | unpaired |
| `Providers/RtDetrLayoutDetector.cs` | 2 | unpaired |
| `Providers/FoundryMistralOcrClient.cs` | 1 | unpaired |
| `Providers/ProviderAdapterHelpers.cs` | 1 | unpaired |

Caveat: this is syntax-only static pairing, not line or branch coverage. It can
miss reflection, extension-method instance syntax, DI-only use, `var`, and
target-typed construction.

## Target behavior inventory

- `ProviderAdapterHelpers` owns option cloning, supported-property validation,
  model selection, typed additional properties, source-output states, exact
  buffered bytes, page frames, polygons, confidence, and structured values.
- `PdfPigDocumentExtractionClient` performs native PDF extraction, preserves one
  output page per PDF page, uses point/bottom-left frames, emits real block
  polygons, and requires both rasterizer and detector for explicit RT-DETR mode.
- `PdfPageRasterizer` performs real PDFium rendering and exact normal-page
  coordinate transforms; it rejects transforms it cannot prove exact.
- `RtDetrLayoutDetector` validates explicit local model path, threshold, SHA-256,
  labels, and ONNX graph before inference.
- `FoundryMistralOcrClient` has a deterministic `HttpMessageHandler` seam and
  credential seam; requests and responses can be tested without network.
- `VisionLlmOcrClient` has an `IChatClient` seam; free-form and structured paths
  can be tested with a fake client without network.
- DI/CU clients cannot be allowed to invoke cloud endpoints. Their deterministic
  mapping helpers can be exercised by reflection and Azure model factories where
  available. CU nullable/missing spans require special attention because direct
  `Span.Offset` access is a regression risk.
- `PdfPigReader` delegates native extraction or an explicit fake client and must
  not silently compose unsupported fallback OCR.
- `IngestionChunkExtensions` traverses source ancestors by reference, deduplicates
  them, resolves regions first, and resolves references only by exact source-page
  identifiers.

## Acceptance checklist

- [ ] **1. exact SourcePage.Identifier PageReference binding.**
- [ ] **2. preserve blank PdfPig pages.**
- [ ] **3. point/bottom-left PdfPig frames and real block polygons.**
- [ ] **4. options cloning/model override/unsupported-property rejection.**
- [ ] **5. explicit RT-DETR prerequisites and bad model path/hash rejection.**
- [ ] **6. rasterizer real PDFium rendering including image-only pages and exact normal-page transform plus rejection of unsupported rotated/offset crop transforms.**
- [ ] **7. no table-cell fabrication from layout detection.**
- [ ] **8. Mistral request-local auth, exact response bytes, detached JsonElement, table markdown without fabricated cells, retry behavior.**
- [ ] **9. Vision structured failure does not silently degrade, freeform rejects non-images, raw native uses RawRepresentation only.**
- [ ] **10. DI/CU mapping where feasible through model factories or focused helpers, including empty cells/spans/roles/confidences and the CU missing-span regression (must not crash or invent spans).**
- [ ] **11. source output disabled/retained/unavailable distinctions.**
- [ ] **12. chunk source-element ancestor/dedupe and page resolution by regions/exact identifier only, never integer parsing/fabricated page 1.**

## Commands

- Restore/build with locked offline inputs:
  `dotnet build .\OcrShape.Tests\OcrShape.Tests.csproj --no-restore`
- Narrow tests:
  `dotnet test .\OcrShape.Tests\OcrShape.Tests.csproj --no-restore`
- Full bounded build:
  `dotnet build .\OcrShape.sln --no-restore --no-incremental`
- Full bounded test:
  `dotnet test .\OcrShape.sln --no-restore`
