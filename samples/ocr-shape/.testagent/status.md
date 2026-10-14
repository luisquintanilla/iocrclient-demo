# Bounded OCR Shape Test Status

## Result

- Strategy: broad, sequential Research -> Plan -> Implement.
- Framework: xUnit 2.9.3 on `net10.0`, `Microsoft.NET.Test.Sdk` 17.14.1.
- Final memory-bounded count: **63 passed, 0 failed, 1 skipped** of 64.
  The skipped test is the explicit external-model RT-DETR geometry test, which
  passed separately before the no-model run.
- All edits and artifacts are under `samples/ocr-shape`.
- Tests use generated in-memory PDFs, fake credentials, fake HTTP handlers, and
  a fake `IChatClient`; no network, cloud, credentials, ports, or timing
  assertions are used.

## Completed acceptance checklist

- [x] **1. exact SourcePage.Identifier PageReference binding.**
  - `DemoOcr.Tests.ProviderAdapterHelpersTests.CreatePageFrame_BindsReferenceToExactSourcePageIdentifier`
  - `DemoOcr.Tests.PdfPigDocumentExtractionClientTests.ExtractPages_BindsEveryReferenceToItsExactSourcePageIdentifier`
- [x] **2. preserve blank PdfPig pages.**
  - `DemoOcr.Tests.PdfPigDocumentExtractionClientTests.ExtractPages_PreservesBlankPageBetweenTextPages`
  - `DemoOcr.Tests.PdfPigReaderTests.ReadAsync_NeverPolicyPreservesPdfPigPagesAndIdentifiers`
- [x] **3. point/bottom-left PdfPig frames and real block polygons.**
  - `DemoOcr.Tests.PdfPigDocumentExtractionClientTests.ExtractPages_UsesPointBottomLeftFrameAndRealBlockPolygon`
- [x] **4. options cloning/model override/unsupported-property rejection.**
  - `DemoOcr.Tests.ProviderAdapterHelpersTests.NormalizeOptions_ClonesCallerOptions`
  - `DemoOcr.Tests.ProviderAdapterHelpersTests.ResolveModel_UsesDefaultAllowsOverrideAndRejectsForbiddenOverride`
  - `DemoOcr.Tests.ProviderAdapterHelpersTests.RejectUnsupportedProperties_ReportsSortedUnsupportedNames`
  - `DemoOcr.Tests.PdfPigDocumentExtractionClientTests.ExtractPages_InvalidLayoutOptionsAreRejected`
- [x] **5. explicit RT-DETR prerequisites and bad model path/hash rejection.**
  - `DemoOcr.Tests.PdfPigDocumentExtractionClientTests.ExtractPages_RtDetrModeRequiresRasterizerAndDetector`
  - `DemoOcr.Tests.RtDetrLayoutDetectorTests.Constructor_BlankAndMissingModelPathsReportExplicitPrerequisite`
  - `DemoOcr.Tests.RtDetrLayoutDetectorTests.Constructor_BadModelHashIsRejectedBeforeOnnxLoad`
  - Real CPU inference also validated the pinned float32 model with RGB values
    rescaled to `[0,1]` at the normal `0.5` confidence threshold.
  - `DemoOcr.Tests.RtDetrLayoutDetectorTests.Detect_NonSquareRenderedPageAlignsWithNativeTextRegion`
    validates real PDFium/CPU geometry at IoU `0.623431`.
- [x] **6. rasterizer real PDFium rendering including image-only pages and exact normal-page transform plus rejection of unsupported rotated/offset crop transforms.**
  - `DemoOcr.Tests.PdfPageRasterizerTests.RenderPage_NormalTextPageUsesRealPdfiumAndExactTransform`
  - `DemoOcr.Tests.PdfPageRasterizerTests.RenderPage_ImageOnlyPageUsesRealPdfiumAndProducesNonBlankPng`
  - `DemoOcr.Tests.PdfPageRasterizerTests.RenderPage_RotatedOrOffsetCropTransformsAreRejected`
    (five cases: rotation, left offset, bottom offset, right mismatch, top mismatch)
- [x] **7. no table-cell fabrication from layout detection.**
  - `DemoOcr.Tests.PdfPigDocumentExtractionClientTests.ExtractPages_NativeLayoutNeverFabricatesTableCells`
  - `DemoOcr.Tests.FoundryMistralOcrClientTests.ExtractPages_TableMarkdownCreatesTableWithoutFabricatedCells`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_StructuredTablePreservesMarkdownWithoutFabricatedCells`
- [x] **8. Mistral request-local auth, exact response bytes, detached JsonElement, table markdown without fabricated cells, retry behavior.**
  - `DemoOcr.Tests.FoundryMistralOcrClientTests.ExtractPages_EachRetryRequestCarriesLocalBearerAuthAndFifthResponseSucceeds`
  - `DemoOcr.Tests.FoundryMistralOcrClientTests.ExtractPages_RetainsExactResponseBytesAndDetachedJsonElement`
  - `DemoOcr.Tests.FoundryMistralOcrClientTests.ExtractPages_TableMarkdownCreatesTableWithoutFabricatedCells`
  - `DemoOcr.Tests.FoundryMistralOcrClientTests.ExtractPages_NonRetryableFailureThrowsImmediately`
  - `DemoOcr.Tests.FoundryMistralOcrClientTests.ExtractPages_FifthServiceUnavailableEndsRetrySequence`
- [x] **9. Vision falls back only for schema capability rejection, records/logs that fallback, rejects malformed output and operational failures, and uses RawRepresentation only.**
  - `DemoOcr.Tests.VisionLlmOcrClientTests.CanFallbackToFreeform_ClassifiesOnlyLocalCapabilityFailures`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_SchemaCapabilityFailureFallsBackOnceAndRecordsProvenance`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_StructuredFailureThrowsWithoutFreeformFallback`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_FreeformRejectsNonImageBeforeCallingChatClient`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_FreeformAnnotationUsesOnlyResponseRawRepresentation`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_StructuredOutOfRangeConfidenceIsRejected`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_PdfRendersEveryPageToPngAndPreservesBlankPageIdentity`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_StructuredSchemaFailureOnPdfFallsBackPerRenderedPage`
  - `DemoOcr.Tests.VisionLlmOcrClientTests.ExtractPages_PdfCancellationStopsBeforeChatCalls`
- [x] **10. DI/CU mapping where feasible through model factories or focused helpers, including empty cells/spans/roles/confidences and the CU missing-span regression (must not crash or invent spans).**
  - `DemoOcr.Tests.ProviderReplayTests.DocumentIntelligence_CompletedLroMapsBlankPagesAndHonorsRawCapture`
  - `DemoOcr.Tests.ProviderReplayTests.ContentUnderstanding_CompletedLroMapsBlankPagesAndHonorsRawCapture`
  - `DemoOcr.Tests.AzureDocumentIntelligenceMappingTests.BuildTableMarkdown_EmptyCellsRemainEmptyWithoutFabrication`
  - `DemoOcr.Tests.AzureDocumentIntelligenceMappingTests.ExtractPageMarkdown_EmptyAndConcreteSpansAreHandledExactly`
  - `DemoOcr.Tests.AzureDocumentIntelligenceMappingTests.RoleAndConfidenceMapping_PreserveAvailableValues`
  - `DemoOcr.Tests.ContentUnderstandingMappingTests.BuildTableMarkdown_EmptyCellsRemainEmptyWithoutFabrication`
  - `DemoOcr.Tests.ContentUnderstandingMappingTests.ExtractPageMarkdown_EmptyAndConcreteSpansAreHandledExactly`
  - `DemoOcr.Tests.ContentUnderstandingMappingTests.MissingSpanMapper_DoesNotCrashOrInventSpan`
  - `DemoOcr.Tests.ContentUnderstandingMappingTests.RoleAndConfidenceMapping_PreserveAvailableValues`
- [x] **11. source output disabled/retained/unavailable distinctions.**
  - `DemoOcr.Tests.ProviderAdapterHelpersTests.CreateSourceOutput_CaptureDisabledMarksDisabledAndDropsRepresentations`
  - `DemoOcr.Tests.ProviderAdapterHelpersTests.CreateSourceOutput_CaptureEnabledWithBytesRetainsNativeAndExactRawContent`
  - `DemoOcr.Tests.ProviderAdapterHelpersTests.CreateSourceOutput_CaptureEnabledWithoutBytesDistinguishesUnavailableRawContent`
  - `DemoOcr.Tests.ProviderAdapterHelpersTests.CreateSourceOutput_CaptureEnabledWithoutRepresentationsMarksBothUnavailable`
- [x] **12. chunk source-element ancestor/dedupe and page resolution by regions/exact identifier only, never integer parsing/fabricated page 1.**
  - `DemoOcr.Tests.IngestionChunkExtensionsTests.EnumerateSourceElements_IncludesAncestorsOnceByReference`
  - `DemoOcr.Tests.IngestionChunkExtensionsTests.GetSourcePages_PrefersRegionPagesAndDeduplicatesThem`
  - `DemoOcr.Tests.IngestionChunkExtensionsTests.GetSourcePages_ResolvesOnlyExactPageIdentifiers`
  - `DemoOcr.Tests.IngestionChunkExtensionsTests.GetSourcePages_DoesNotParseIntegerReferencesOrFabricatePageOne`

## Sequential phase validation

| Phase | Command/filter | Result |
|---|---|---|
| Provider primitives | `dotnet test .\OcrShape.Tests\OcrShape.Tests.csproj --no-restore --filter "FullyQualifiedName~ProviderAdapterHelpersTests\|FullyQualifiedName~RtDetrLayoutDetectorTests\|FullyQualifiedName~PdfPageRasterizerTests"` | 15 passed |
| PdfPig/reader | filter `FullyQualifiedName~PdfPig` | 8 passed |
| Mistral/Vision | filter `FoundryMistralOcrClientTests\|VisionLlmOcrClientTests` | 8 passed |
| DI/CU mapping | filter `FullyQualifiedName~MappingTests` | 7 passed |
| Ingestion chunks | filter `FullyQualifiedName~IngestionChunkExtensionsTests` | 4 passed |

Later replay, fallback, real-geometry, PDF vision, and CU page-mapping additions
raised the final discovered count to 64.

## Test-gap analysis

The mandatory `test-gap-analysis` review was invoked against the final bounded
source/test set. Its .NET extension discovery helper was unavailable, so xUnit
semantics were reviewed directly.

Findings fixed before final validation:

1. Vision free-form source output retained the wrapper `ChatResponse` rather
   than only `RawRepresentation`. Production now retains only
   `response.RawRepresentation`, and the test distinguishes both object
   identities.
2. CU mappers dereferenced missing provider spans. A focused null-preserving
   helper now returns `null` rather than crashing or inventing offset/length.
3. Added the top crop-box mismatch, native/content unavailable source-output
   state, invalid layout confidence boundaries, terminal fifth 503 behavior,
   and out-of-range structured Vision confidence.

Empirical pseudo-mutations:

- `response.RawRepresentation` -> `response`: killed by
  `ExtractPages_FreeformAnnotationUsesOnlyResponseRawRepresentation` (1 failed).
- missing CU span `null` -> fabricated `{ Offset = 0, Length = 0 }`: killed by
  `MissingSpanMapper_DoesNotCrashOrInventSpan` (1 failed).

Both mutations were immediately removed. Final validation below confirms no
mutation remains. No remaining in-scope requirement gap was found.

The final geometry review injected the original `[height,width]` target-size
ordering and reran only
`Detect_NonSquareRenderedPageAlignsWithNativeTextRegion`. The test failed with
best IoU `0.000000`, killing the mutation. Restoring `[width,height]` returned
IoU `0.623431`, and the full suite then passed. The completed-LRO replays also
found and fixed lower-case DI unit mapping and CU's null
`ProcessingLocation` conversion.

## Assertion-quality review

The mandatory `assertion-quality` review was invoked against all final xUnit
files. Its .NET extension discovery helper was unavailable, so xUnit assertions
were classified directly.

- Assertion-free tests: 0.
- Trivial-only tests: 0.
- Tautological identity/round-trip tests: 0.
- The suite uses concrete equality, exact bytes, exception type/message/parameter,
  collection order/content, runtime type, null/negative, numeric comparison,
  object identity, state/call-count, and deep record assertions.
- Multi-observable operations assert secondary state: request captures and delay
  schedule, source statuses and artifact bytes/media type, coordinate frame and
  polygon, page identifiers and references, call counts and output markdown.
- The final replay/fallback additions assert request method and poll URI, exact
  final wire bytes, blank-page preservation, retained/disabled raw state,
  structured-fallback call count, standard warning event, source-output
  identity, provenance values, and native raw identity.
- The real geometry test combines collection, structural, and numeric
  assertions and fails on the demonstrated axis swap rather than checking only
  that a detection exists.
- Weak assertion fixes included exact raw object identity, exact response bytes,
  exact retry schedule/count, exact page/reference identifiers, and explicit
  empty-cell/span assertions.

## Final validation

Restore was performed from only `local-feed` and `validation-feed`; no remote
source was used.

```powershell
dotnet build .\samples\ocr-shape\OcrShape.csproj --no-restore --no-incremental --verbosity minimal
```

Result: **Build succeeded**, 0 warnings, 0 errors.

```powershell
dotnet test .\samples\ocr-shape\OcrShape.Tests\OcrShape.Tests.csproj --no-restore --verbosity minimal
```

Result:

```text
Passed!  - Failed: 0, Passed: 63, Skipped: 1, Total: 64, Duration: 2 s
```

The exact `10.10.0-dev.e2e.cf60fa88.1` Microsoft Extensions package family
remains the bounded production dependency.

The final focused Vision run passed all 12
`VisionLlmOcrClientTests`, including the three PDF rendering/fallback/
cancellation regressions. The model-backed geometry test had already passed
with IoU `0.623431`; it was intentionally not rerun in the final
memory-constrained no-model pass.

The real ONNX smoke run used the pinned
`3bf2fb0ee6df87435b7ae47f0f3930ec3dc97ec56fd824acc6d57bc7a6b89ef2`
model and exited successfully:

```text
page=1 observations=1 detections=1 maxScore=0.698619
page=1 bestTextDetectionIoU=0.623431
page=2 observations=0 detections=1 maxScore=0.948161
pages=2 detections=2
```

Page 2 is image-only, so this also validates real PDFium rasterization rather
than synthetic word-box rendering.

## Live CU spanless Markdown regression

The live `prebuilt-document` result for the three-page synthetic fixture
returned document-level Markdown with two documented `<!-- PageBreak -->`
markers, but no page spans and no paragraphs. The adapter previously emitted
three empty pages. It now uses page-break metadata only when every page span is
absent, preserves the third blank page, and rejects nonempty spanless Markdown
when the marker count cannot match the provider page count.

`ContentUnderstanding_CompletedLroMapsBlankPagesAndHonorsRawCapture` now replays
the spanless/page-break response shape. Both raw-output variants passed:

```text
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2
```

`ExtractPageMarkdown_SpanPresentPathWinsOverPageBreakFallback` preserves the
original span-based mapping path.
`ExtractPageMarkdown_SpanlessMarkerMismatchIsRejected` verifies that a
nonempty spanless response with an inconsistent page-break count fails
explicitly instead of dropping content.

A keyless live verification against the working East US endpoint produced:

```text
page=1 markdown=The validation code is ORBIT-742.
page=2 markdown=The backup code is MAPLE-915.
page=3 markdown=
```
