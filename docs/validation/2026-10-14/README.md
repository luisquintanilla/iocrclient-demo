# Integrated extraction validation: 2026-10-14

This review packet preserves measured results, not release approval. It contains
only generated synthetic input, normalized outputs and safe persisted source
evidence. It contains no credentials, cloud resource/account identifiers, user
paths, private documents, raw SDK/service responses, model weights or databases.

## What was measured

The fixture is a three-page A4 PDF: page 1 contains `ORBIT-742`, page 2 contains
`MAPLE-915`, and page 3 is blank. Full acceptance uses actual extraction, shared
Documents, MEDI SectionChunker, the typed citation writer, SqliteVec repeat
ingestion/reopen and exact originating-page attribution. Invented citation IDs
and unsupported quotes are rejected; the unsupported question requires abstention.

| Run | Outcome | What it proves |
| --- | --- | --- |
| `live-final-02` | All five paths passed in one sequential run | DI, CU, Mistral, Vision and native PdfPig with real embedding/chat deployments, grounded answers and source-supported citations. |
| `native-final-post-vision` | Passed | Genuine native PdfPig/MEDI/SqliteVec wiring with explicitly labelled embedding/chat doubles. |
| `rtdetr-final` | Passed | Actual pinned CPU ONNX model on rendered PDF pages; persisted geometry IoUs 0.7183 and 0.6912, required minimum 0.5. AI remains doubled in this run. |
| Provider regressions | 63 passed, 0 failed, 1 skipped | Credential-free mapping, ownership, raw-policy, fallback and PDF vision behavior. The external-model geometry test is not reloaded in this low-memory run. |
| MEDI regressions | 152 passed, 0 failed, 0 skipped | Complete net10 suite with isolated MarkItDown prerequisites; production builds cover all five declared TFMs. |

The immutable candidate is `10.10.0-dev.e2e.cf60fa88.1`. Its original manifest
SHA-256 is `B73604770D1E41E17ED202A982583D73278B05AA195CB6D81DC92D8BF149A01F`.
Build/package validation is not public API approval. Source changes in this demo
remain a separate review unit from the Documents/Extraction PRs.

## Files and provenance

`runs/` contains original safe run receipts, normalized shared-document Markdown,
normalized synthetic answers, before/after record identities, synthetic PDFs,
and derived persisted records with page frames/polygons/confidence meaning.
Exported records do not contain embedding vectors or raw provider output.
`source-inspection/` contains actual browser DOM evidence and screenshots;
the pre-fix RT-DETR image is labelled as failed evidence, not final alignment.
`tests/` contains a sanitized per-test provider result export.
`manifest.json` lists exact relative file paths, sizes and SHA-256 fingerprints.

A separate private local snapshot keeps the implementation overlay, immutable
runtime packages, original logs/results and databases outside the worktree. It
is not Git-bound evidence and is not a remotely backed-up archive. Model weights,
credential stores and environment/user-secret files are excluded there too.
Do not commit private snapshots or publish original raw logs without review.

## Failures are evidence, not clutter

Earlier receipts are retained separately from the final pass:

- Incorrect ingestion settings appended duplicate rows; incremental replacement
  fixes this while preserving stable citation identity across fresh row keys.
- Non-square RT-DETR geometry used swapped target dimensions. The pinned graph
  requires `[width,height]`; the real regression fails at IoU 0.0 under the wrong
  order and passes at IoU 0.623431 under the correct order.
- Co-located proof outputs collided; project-specific output paths prevent a
  neighboring proof build from removing required runtime files.
- An advertised CU endpoint returned route404; another existing endpoint had an
  active route. No resource or analyzer was provisioned/reconfigured.
- Live CU returned Markdown with documented page-break markers but no paragraph
  or page spans. The adapter now preserves that content, checks exact marker/page
  cardinality, and retains the authoritative span-present branch when available.
- One earlier live Vision answer failed the required citation syntax. Later
  passes do not erase that failure or prove deterministic model compliance.

## Revisit these limits

This is a controlled wiring/fidelity fixture, not a representative quality
benchmark. It does not establish complex-table accuracy, handwriting recognition,
reading-order quality, general OCR quality, robust citation reliability, or
performance across a real corpus. Blank source-page preservation does not imply
perfect blank-page transcription by an LLM.

Original provider responses were retained during each operation where supported
but deliberately not written into this packet. Consequently, normalized output
and persisted source evidence can be reanalyzed here, but every SDK-to-wire
mapping decision cannot be reconstructed from a raw response archive.

Rotation, offset crop and unknown coordinate transforms remain explicit
unsupported conditions without a verified mapping. Local image-only RT-DETR is
layout detection, not OCR. Model licensing evidence is publisher metadata, not a
completed redistribution review. A passing source overlay is not general
detection accuracy.

To reproduce, import the exact coherent candidate and follow `samples/README.md`
and `docs/SETUP.md`. Live calls remain opt-in and use the SDK
`DefaultAzureCredential`; keep endpoint/deployment configuration process-local
or in user secrets, never in this packet.
