# Samples

Each sample demonstrates one consumer claim. Provider calls are optional and credentials stay
outside the repository. The canonical proof is the in-memory page-observation sequence finalized
into the provisional Documents tree.

## Provisional local stack

The samples consume the exact unmerged `dotnet/extensions` ancestry below:

| Slice | Commit |
| --- | --- |
| Documents abstractions | `d35d5cb74695c4ba970d6ad198006b84354badc0` |
| DocumentExtraction | `2d28dfd57712b55f941fbb1dbf41311eeaee8c8b` |
| DataIngestion composition bridge | `8ae3d2436d105c8449a712d58618ff444b617f7b` |

Run `scripts/build-local-feed.sh` to pack the coherent `10.10.0-dev` line into `local-feed/`.
The script uses an external source checkout and does not vendor or publish the experimental APIs.
See [`local-feed/README.md`](../local-feed/README.md) for provenance.

`IDocumentExtractionClient` streams `DocumentExtractionPage` observations. Consumers supply the
document identifier and explicitly finalize with `DocumentExtractionDocumentBuilder` or the
`ToDocumentExtractionResult*` helpers. Provider observation IDs are local duplicate-detection keys,
not durable document IDs.

The shared result does not expose raw provider payloads, credentials, signed URLs, private
identifiers, unsupported geometry/confidence claims, or fabricated grounding. Image content is
optional; the safe sample reports presence only. The in-memory Documents tree is canonical. Any
JSON round-trip is an experimental, separate proof.

## Samples

| Sample | What it shows |
| --- | --- |
| `01-vision-ocr.cs` | Vision LLM transcription as page observations. |
| `02-document-intelligence.cs` | Document-native pages, tables, and images. |
| `03-content-understanding.cs` | CU page spans, including safe missing-span handling. |
| `04-mistral-ocr.cs` | Mistral page observations and structural observations. |
| `05-one-loop-four-clients.cs` | One consumer loop across four adapters. |
| `06-medi-pipeline.cs` | Provisional extraction-to-MEDI bridge and page provenance. |
| `07-e2e-rag.cs` | OCR, ingestion, retrieval, and page-cited answer. |
| `08-pdfpig-reader.cs` | Native PdfPig, fallback policy, and whole-document composition. |
| `09-images-and-uris.cs` | Safe in-memory image observation facts. |
| `10-eval-ocr-vs-pdfpig.cs` | OCR versus native PdfPig downstream comparison. |
| `11-vision-structured-output.cs` | Structured transcription and typed extraction composition. |
| `14-ollama-glm-ocr.cs` | Local Ollama vision adapter. |
| `15-ocr-engine-comparison.cs` | Cloud/local engines through one page-observation seam. |
| `16-streaming.cs` | Page streaming followed by explicit in-memory finalization. |
| `17-deterministic-extraction.cs` | Credential-free contract, edge-case, and ownership proof. |

## Run

```bash
scripts/build-local-feed.sh
dotnet run --project samples/proofs/DeterministicExtractionProof.csproj
dotnet run samples/16-streaming.cs
```

Provider samples require the corresponding endpoint and keyless identity configuration. If a live
provider is unavailable, keep the output as a `TODO run against <provider>` rather than inventing
results. Do not capture or commit provider payloads, document-derived PII, credentials, or tokens.
