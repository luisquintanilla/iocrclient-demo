# Bounded OCR Shape Test Plan

## Exchange-fidelity follow-up

1. Add real SDK transport-replay cases for DI and CU complex tables, including
   merged spans, roles, imperfect transcription, explicit empty cells, absent
   slots, two page frames and exact polygons, with raw capture on/off.
2. Add Mistral HTTP-response cases for current `id`/`content`/`format` tables:
   HTML and Markdown, unique page marker ordering, identical table bodies with
   distinct IDs, unknown placement, retained/detached raw data and opt-out.
   Preserve legacy `markdown` fixtures. Demonstrate failures before repair.
3. Add a Vision structured response case preserving imperfect transcription,
   unknown geometry, table dimensions without invented cells, and self-reported
   confidence while raw capture is disabled.
4. Repair only demonstrated adapter defects. Run focused tests, then the
   existing whole bounded suite/build without a model or network. Retain the
   existing optional external-model skip honestly.
5. Run test-gap/assertion-quality review, record exact commands, test names,
   limitations and disposition in status and a mapping-fidelity guide.

No new chunking policy, core API, candidate repack, live call, credentials,
publication, model loading or preview-server mutation.

Implementation is sequential. Each phase is built and tested before the next.

## Final compatibility/replay phase

- `VisionLlmOcrClientTests.ExtractPages_SchemaCapabilityFailureFallsBackOnceAndRecordsProvenance`
  verifies the capability-only branch, warning event, two calls, raw identity,
  and explicit source provenance.
- `ProviderReplayTests.DocumentIntelligence_CompletedLroMapsBlankPagesAndHonorsRawCapture`
  runs a real DI client over a fake transport for retained and disabled raw
  output.
- `ProviderReplayTests.ContentUnderstanding_CompletedLroMapsBlankPagesAndHonorsRawCapture`
  runs the equivalent CU completed-LRO replay.
- `RtDetrLayoutDetectorTests.Detect_NonSquareRenderedPageAlignsWithNativeTextRegion`
  performs real CPU inference when `OCR_SHAPE_RTDETR_MODEL` is configured and
  requires IoU greater than 0.5 against the known native text region.
- `VisionLlmOcrClientTests.ExtractPages_PdfRendersEveryPageToPngAndPreservesBlankPageIdentity`
  verifies real PDFium rendering, PNG chat inputs, raw outputs, original page
  numbering/frames, and blank-page preservation.
- `VisionLlmOcrClientTests.ExtractPages_StructuredSchemaFailureOnPdfFallsBackPerRenderedPage`
  verifies that capability-only structured fallback composes with per-page PDF
  rendering rather than sending the PDF directly.
- `VisionLlmOcrClientTests.ExtractPages_PdfCancellationStopsBeforeChatCalls`
  verifies cancellation before any page-level chat operation.

Validation: run the focused filters, then a zero-warning production build and
the whole suite with the pinned model environment variable configured.

## Phase 1 - Test project, helpers, provider primitives

Create `OcrShape.Tests` (xUnit, SDK-style, offline package versions), a bounded
solution, deterministic PDF fixture builder, fake Azure credential, and fake
HTTP handler.

Planned tests:

- `ProviderAdapterHelpersTests.CreatePageFrame_BindsReferenceToExactSourcePageIdentifier`
  - checklist 1.
- `ProviderAdapterHelpersTests.NormalizeOptions_ClonesCallerOptions`
- `ProviderAdapterHelpersTests.ResolveModel_UsesDefaultAllowsOverrideAndRejectsForbiddenOverride`
- `ProviderAdapterHelpersTests.RejectUnsupportedProperties_ReportsSortedUnsupportedNames`
  - checklist 4.
- `ProviderAdapterHelpersTests.CreateSourceOutput_CaptureDisabledMarksDisabledAndDropsRepresentations`
- `ProviderAdapterHelpersTests.CreateSourceOutput_CaptureEnabledWithBytesRetainsNativeAndExactRawContent`
- `ProviderAdapterHelpersTests.CreateSourceOutput_CaptureEnabledWithoutBytesDistinguishesUnavailableRawContent`
  - checklist 11.
- `RtDetrLayoutDetectorTests.Constructor_BlankAndMissingModelPathsReportExplicitPrerequisite`
- `RtDetrLayoutDetectorTests.Constructor_BadModelHashIsRejectedBeforeOnnxLoad`
  - checklist 5.
- `PdfPageRasterizerTests.RenderPage_NormalTextPageUsesRealPdfiumAndExactTransform`
- `PdfPageRasterizerTests.RenderPage_ImageOnlyPageUsesRealPdfiumAndProducesNonBlankPng`
- `PdfPageRasterizerTests.RenderPage_RotatedOrOffsetCropTransformsAreRejected`
  - checklist 6.

Validation: build and filter Phase 1 tests.

## Phase 2 - PdfPig extraction and reader

Planned tests:

- `PdfPigDocumentExtractionClientTests.ExtractPages_BindsEveryReferenceToItsExactSourcePageIdentifier`
  - checklist 1.
- `PdfPigDocumentExtractionClientTests.ExtractPages_PreservesBlankPageBetweenTextPages`
  - checklist 2.
- `PdfPigDocumentExtractionClientTests.ExtractPages_UsesPointBottomLeftFrameAndRealBlockPolygon`
  - checklist 3.
- `PdfPigDocumentExtractionClientTests.ExtractPages_RtDetrModeRequiresRasterizerAndDetector`
  - checklist 5.
- `PdfPigDocumentExtractionClientTests.ExtractPages_NativeLayoutNeverFabricatesTableCells`
  - checklist 7.
- `PdfPigReaderTests.ReadAsync_NeverPolicyPreservesPdfPigPagesAndIdentifiers`
- `PdfPigReaderTests.ReadAsync_AllPagesUsesExplicitClientAndCountsOneCall`
- `PdfPigReaderTests.ReadAsync_FallbackPolicyFailsRatherThanSilentlyComposingCloudOcr`

Validation: build and filter PdfPig tests.

## Phase 3 - Mistral and Vision provider seams

Planned tests:

- `FoundryMistralOcrClientTests.ExtractPages_EachRetryRequestCarriesLocalBearerAuthAndFifthResponseSucceeds`
- `FoundryMistralOcrClientTests.ExtractPages_RetainsExactResponseBytesAndDetachedJsonElement`
- `FoundryMistralOcrClientTests.ExtractPages_TableMarkdownCreatesTableWithoutFabricatedCells`
- `FoundryMistralOcrClientTests.ExtractPages_NonRetryableFailureThrowsImmediately`
  - checklist 8.
- `VisionLlmOcrClientTests.ExtractPages_StructuredFailureThrowsWithoutFreeformFallback`
- `VisionLlmOcrClientTests.ExtractPages_FreeformRejectsNonImageBeforeCallingChatClient`
- `VisionLlmOcrClientTests.ExtractPages_FreeformAnnotationUsesOnlyResponseRawRepresentation`
- `VisionLlmOcrClientTests.ExtractPages_StructuredTablePreservesMarkdownWithoutFabricatedCells`
  - checklist 9 and checklist 7.

Validation: build and filter provider seam tests.

## Phase 4 - DI/CU focused mapper contracts

Use model factories where public construction is available and focused reflection
for deterministic private mapping helpers. Do not instantiate clients in a way
that sends requests.

Planned tests:

- `AzureDocumentIntelligenceMappingTests.BuildTableMarkdown_EmptyCellsRemainEmptyWithoutFabrication`
- `AzureDocumentIntelligenceMappingTests.ExtractPageMarkdown_EmptySpansReturnsEmpty`
- `AzureDocumentIntelligenceMappingTests.RoleAndConfidenceMapping_PreserveAvailableValues`
- `ContentUnderstandingMappingTests.BuildTableMarkdown_EmptyCellsRemainEmptyWithoutFabrication`
- `ContentUnderstandingMappingTests.ExtractPageMarkdown_EmptySpansReturnsEmpty`
- `ContentUnderstandingMappingTests.MissingSpanMapper_DoesNotCrashOrInventSpan`
- `ContentUnderstandingMappingTests.RoleAndConfidenceMapping_PreserveAvailableValues`
  - checklist 10 and checklist 7.

If the Azure SDK exposes no viable factory for a complex model, the closest
pure mapping helper will be tested and the precise blocked subcase recorded.

Validation: build and filter mapping tests.

## Phase 5 - Ingestion chunks and final integration review

Planned tests:

- `IngestionChunkExtensionsTests.EnumerateSourceElements_IncludesAncestorsOnceByReference`
- `IngestionChunkExtensionsTests.GetSourcePages_PrefersRegionPagesAndDeduplicatesThem`
- `IngestionChunkExtensionsTests.GetSourcePages_ResolvesOnlyExactPageIdentifiers`
- `IngestionChunkExtensionsTests.GetSourcePages_DoesNotParseIntegerReferencesOrFabricatePageOne`
  - checklist 12.

Then:

1. Run all bounded tests.
2. Run full bounded non-incremental solution build.
3. Run full bounded solution tests with a fresh build.
4. Invoke `test-gap-analysis` against the ten bounded source files and final
   tests, fix every in-scope gap, and rerun.
5. Invoke `assertion-quality` against final tests, replace weak assertions, add
   secondary observables where applicable, and rerun.
6. Write `.testagent/status.md` with exact passing names, checklist mapping,
   commands, findings, fixes, and any explicit blockers.
