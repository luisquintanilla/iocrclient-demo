# Provisional local feed

These packages are unofficial local builds used only by this demo. They are not public releases
and must not be treated as a supported Microsoft package source.

## Exact provenance

`scripts/build-local-feed.sh` checks out the composition bridge commit below and packs one coherent
`10.10.0-dev` package line:

| Slice | Commit |
| --- | --- |
| Documents abstractions | `d35d5cb74695c4ba970d6ad198006b84354badc0` |
| DocumentExtraction abstractions and implementation | `2d28dfd57712b55f941fbb1dbf41311eeaee8c8b` |
| DataIngestion composition bridge | `8ae3d2436d105c8449a712d58618ff444b617f7b` |

The commits are an ancestry chain. The script uses the final commit as one source snapshot, so AI,
DataIngestion, Documents, DocumentExtraction, and the bridge share assembly identities. The source
checkout is created outside this repository and is never copied into the sample.

```bash
WORK="$HOME/dev/extensions-n-g4" scripts/build-local-feed.sh
```

The checked-in `.nupkg` files are only a feed placeholder. Rebuild the feed before restoring after
changing the provisional stack. Do not replace the package line with a guessed public version.

## Proof boundary

The required proof is the in-memory page-observation sequence finalized by
`DocumentExtractionDocumentBuilder`. `samples/17-deterministic-extraction.cs` is credential-free
and does not serialize or freeze a neutral-tree shape. Provider payloads, credentials, bearer
tokens, private identifiers, geometry, confidence, and document-derived evidence are not emitted.
Any future JSON round-trip is experimental and separate from the canonical in-memory proof.
