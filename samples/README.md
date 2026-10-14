# Samples

Each sample demonstrates one consumer claim. Provider calls are optional and credentials stay
outside the repository. The E2E proof exercises extraction into shared Documents, actual MEDI
processing/chunking, embeddings, a typed writer, persisted SqliteVec retrieval and cited answers.

## Experimental integrated stack

The validation branch consumes one exact candidate: `10.10.0-dev.e2e.cf60fa88.1`.
It is not a published release or upstream API approval. Its source inputs are:

| Slice | Commit |
| --- | --- |
| Documents PR #7835 | `37dad1a0837de86317a716c81805560a1cea18dc` |
| Extraction PR #7588, including shared Documents | `1fc95b5b77b48e085b49cf25fd1b3e4797825fa9` |
| MEDI Preview 2 baseline | `e124c123afeeda2f271f3b99a70eb3cfe187a471` |
| MEDI #7488 proposal baseline | `9d81ab6f2cad28525ae6b3a360d147b8a220276f` |

The integration manifest records the reconciliation, working-tree provenance, package hashes and
build evidence. Import its complete 16-package closure with `scripts/Import-E2ECandidate.ps1`;
packages go into ignored `validation-feed/`. Different bytes cannot overwrite a version.
The older `local-feed/` is historical, not this candidate's source. Never rebuild the same candidate
version from modified source or substitute public main for the selected MEDI baseline.

`IDocumentExtractionClient` streams `DocumentExtractionPage` observations. Consumers supply the
document identifier and explicitly finalize with `DocumentExtractionDocumentBuilder` or the
`ToDocumentExtractionResult*` helpers. Provider observation IDs are local duplicate-detection keys,
not durable document IDs.

Shared Documents preserves explicit source frames, polygons, confidence meaning, table structure
and operation-scoped raw output. Raw retention is on by default, with a per-call opt-out. Native
SDK objects and original wire bytes are distinct capabilities; neither is silently reserialized
into the other. Raw output is not logged, embedded or persisted into vector records.

Source pages resolve by exact registered frame identifiers, never section indexes or guessed
page numbers. The hero persists safe source attribution and verifies the original file hash before
displaying actual PDF pages with geometry. Unknown coordinate systems, rotation and crop offsets
without a verified transform are reported as unsupported rather than silently projected.

## Samples

| Sample | What it shows |
| --- | --- |
| `01-vision-ocr.cs` | Vision LLM transcription as page observations. |
| `02-document-intelligence.cs` | Document-native pages, tables, and images. |
| `03-content-understanding.cs` | CU page spans, including safe missing-span handling. |
| `04-mistral-ocr.cs` | Mistral page observations and structural observations. |
| `05-one-loop-four-clients.cs` | One consumer loop across native PdfPig plus configured cloud adapters; cloud calls need `--allow-live`. |
| `06-medi-pipeline.cs` | Direct shared Documents ingestion, chunk source elements and page provenance. |
| `07-e2e-rag.cs` | The same full E2E runner, with native/offline and opt-in live modes. |
| `08-pdfpig-reader.cs` | Native PdfPig, fallback policy, and whole-document composition. |
| `09-images-and-uris.cs` | Safe in-memory image observation facts. |
| `10-eval-ocr-vs-pdfpig.cs` | OCR versus native PdfPig downstream comparison. |
| `11-vision-structured-output.cs` | Structured transcription and typed extraction composition. |
| `12-ingestion-pipeline.cs` | Full E2E runner through the real MEDI pipeline and typed writer. |
| `13-composition.cs` | Native text versus real-page RT-DETR layout with a pinned model; no implicit cloud fallback. |
| `14-ollama-glm-ocr.cs` | Local Ollama vision adapter. |
| `15-ocr-engine-comparison.cs` | Cloud/local engines through one page-observation seam. |
| `16-streaming.cs` | Page streaming followed by explicit in-memory finalization. |
| `17-deterministic-extraction.cs` | Credential-free contract, edge-case, and ownership proof. |

## Run

```powershell
pwsh .\scripts\Import-E2ECandidate.ps1 -ManifestPath <integration-manifest.json>
dotnet run --project .\samples\proofs\DeterministicExtractionProof.csproj
dotnet run --project .\samples\proofs\FullE2EProof.csproj -- --mode offline --provider pdfpig
dotnet run --project .\samples\proofs\FullE2EProof.csproj -- --mode live --provider all --allow-live
```

Add `--layout rtdetr --model <local-model.onnx> --model-sha256 <pinned hash>` to the native
E2E command for actual ONNX inference through MEDI and persisted retrieval. Model setup and
licensing boundaries are in [`docs/SETUP.md`](../docs/SETUP.md). The controlled non-square fixture
also checks layout/text overlap (IoU at least 0.5), not just that a model returned a detection.

Offline E2E uses genuine native PdfPig, MEDI and SqliteVec, with explicitly labelled deterministic
embedding/chat doubles. It does not prove live model quality. Live E2E uses the configured cloud
extraction, embedding and chat services on synthetic documents, requires explicit consent, and
reports unavailable services as blocked. Each fresh output directory receives a sanitized
`run.json`; never interpret skipped, blocked or unrun paths as passes.

### Recorded validation

| Boundary | Evidence and limit |
| --- | --- |
| Shared Documents/MEDI candidate | All five declared production TFMs compile; complete net10 MEDI suite passes 152/152 with zero skips. |
| Provider mapping and compatibility | Real DI/CU SDK completed-LRO replay, raw default/opt-out, capability-only fallback, actual-page PDF vision and CU spanless/page-break regressions pass. Final low-memory suite: 63 passed, one external-model test not reloaded. |
| Actual local model | Earlier model-enabled provider suite passes 59/59. Non-square geometry regression measures IoU 0.623431; swapping target-size axes makes it fail at 0.0. |
| Persisted full pipeline | Native and RT-DETR runs pass real MEDI/SqliteVec repeat ingestion, reopen, source-page attribution and citation rejection. RT-DETR persisted IoUs are 0.7183 and 0.6912, above the required 0.5. Embedding/chat remain labelled doubles. |
| Source inspection | Actual browser verifies source-file hash, rendered PDF pixels, supporting text and aligned corrected polygons; eight projection tests pass. |
| Consumers | Samples 01-16 compile; deterministic proof passes 15 checks; final hero/proof graph and benchmark builds have zero warnings/errors. |
| Live acceptance | `live-final-02`: all five providers pass in one sequential run with DefaultAzureCredential, actual extraction, real embeddings/chat, repeat/reopened SqliteVec storage, source-supported citations and unsupported-question abstention. Generated synthetic fixture only, not general quality/reliability evidence. |

Provider test names and quality-review evidence are in `ocr-shape/.testagent/status.md`.
Full-pipeline receipts are in ignored `output/e2e/native-final-post-vision/run.json` and
`output/e2e/rtdetr-final/run.json`. The full live receipt is
`output/e2e/live-final-02/run.json`; these controlled fixtures are not a general accuracy benchmark.
Earlier failed/blocked runs remain diagnosis evidence, including a live Vision citation-format
failure. CU's live fix preserves valid document-level Markdown when the service supplies no page
spans or paragraphs, using documented page-break markers with exact page-count validation.

The hero selects `OCR:Provider`: `vision` (default), `document-intelligence`,
`content-understanding`, `mistral`, or `pdfpig`. Cloud extraction requires its endpoint and keyless
identity; hero embedding/chat configuration is required independently. Do not commit raw provider
payloads, document-derived PII, credentials or tokens.

Optional hero layout: select `OCR:Provider=pdfpig`, `OCR:PdfPigLayout=rtdetr`, and supply
`OCR:LayoutModelPath` plus `OCR:LayoutModelSha256`. Native is the default. The detector is owned by
DI and requires the compatible export documented in provider setup. `OCR:CaptureRawOutput=false`
opts out per ingestion call; `OCR:Model` supplies a per-call override rather than mutating client
defaults. Sample `13` exposes the same explicit model/hash choice.
