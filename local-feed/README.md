# `local-feed/` — unofficial local dev packages (READ THIS)

**These are NOT official Microsoft packages.** They are local, unofficial builds produced solely so
this demo is clone-and-run while the APIs it showcases are still in review. Do not depend on them in
real projects, and do not redistribute them as if they were shipped Microsoft packages.

## What these are

`IOcrClient` and the chunk page-provenance shown in this demo are **proposed** in open pull requests
against [dotnet/extensions](https://github.com/dotnet/extensions) and are **not on nuget.org yet**.
Rather than vendor a hand-copied snapshot, `../scripts/build-local-feed.sh` packs the *real*
dotnet/extensions source into this folder at version `10.8.0-dev`, so the samples bind to the actual
`Microsoft.Extensions.*` types. `../nuget.config` resolves these from `local-feed`; everything else
(Azure SDKs, OpenAI, PdfPig, vector-store connectors) resolves from nuget.org.

## Provenance (what went into the build)

Packed **2026-07-01** at version `10.8.0-dev` from this composition:

- base: `dotnet/extensions` branch **`data-ingestion-preview2`** (latest MEDI: non-generic `IngestionChunk`)
- **+ PR [#7516](https://github.com/dotnet/extensions/pull/7516)** — opt-in element→chunk metadata propagation (targets `data-ingestion-preview2`)
- **+ PR [#7588](https://github.com/dotnet/extensions/pull/7588)** — `IOcrClient` (the OCR seam; additive `Ocr/` folders grafted from the PR head, since #7588 targets `main`)
- two one-line const edits the grafted files depend on (an experimental diagnostic id and an OpenTelemetry const) — see the build script

The exact recipe (and the branch/PR heads used) lives in
[`../scripts/build-local-feed.sh`](../scripts/build-local-feed.sh); it reproduces this feed from
public GitHub refs so these binaries are verifiable, not opaque.

## Committed contents

Only the **runtime** `*.nupkg` files are committed. `*.symbols.nupkg` are intentionally left out
(`.gitignore`) — they are not needed to run the samples.

## License

The packaged code is from `dotnet/extensions`, licensed under the
**[MIT License](https://github.com/dotnet/extensions/blob/main/LICENSE)**
(Copyright (c) .NET Foundation and Contributors). The MIT license text and copyright are retained
inside each `.nupkg`. This repository's own sample code is likewise MIT (see `../LICENSE`).

## When to delete this feed

The moment #7516 and #7588 ship on nuget.org, this whole folder becomes unnecessary:

1. delete `local-feed/`,
2. remove the `<clear/>` + `local-feed` source from `../nuget.config`,
3. bump the samples to the published package versions.
