# Scripts

`build.mjs` and `serve.mjs` live at the repo root because npm scripts call them directly.

Add talk-specific helpers here only when they repeat across talks. Keep one-off demo steps in `samples/README.md` or speaker notes.

## Integrated candidate feed

`Import-E2ECandidate.ps1 -ManifestPath <manifest.json>` validates all 16 runtime package hashes
and actual package versions before importing into ignored `validation-feed/`. It rejects duplicate
filenames and different bytes at an existing version. The source manifest and import receipt
remain separate; importing does not change provenance or publish anything.

`Build-E2EFeed.ps1` replaces the older fixed-ancestry bridge recipe. Supply the frozen integration
manifest, its already-reconciled source worktree, a fresh destination and a **new version**:

```powershell
pwsh .\scripts\Build-E2EFeed.ps1 -SourceManifestPath <original-manifest.json> `
  -Source <integration-worktree> -Destination <fresh-candidate-directory> `
  -Version 10.10.0-dev.e2e.youridentifier.1 -ValidateOnly
```

Remove `-ValidateOnly` to pack. The recipe checks HEAD, the complete tracked patch, source overlay,
deleted files, untracked inputs and the recorded SDK before building the same 16-library closure.
It retains all project TFMs, treats warnings as errors, records a truthful working-tree label and
does not ignore failed feeds. A partial candidate stays partial; never overwrite it.
`build-local-feed.sh` forwards the same arguments to PowerShell 7 rather than fetching old refs.
The immutable original candidate already has its frozen recipe and source overlay in its manifest.

## Acceptance helpers

`node --test .\scripts\source-viewer.test.mjs` checks coordinate projection and explicit unsupported
frame conditions. `verify-source-browser.mjs` verifies an already-running hero source URL using
an **isolated** local browser debugging endpoint; it saves a DOM evidence JSON and screenshot,
checks actual PDF canvas/geometry and supporting text, and does not invoke cloud services.

Full native/live pipeline commands and the distinction between real components and labelled
embedding/chat doubles are in [`samples/README.md`](../samples/README.md).

Build outputs and intermediate/lock paths are isolated per project, including the
co-located proof executables. Building the deterministic proof must not remove live
SDK DLLs from the full E2E proof. Run the proof via its project rather than an old
shared `bin/Debug/net10.0` path.
