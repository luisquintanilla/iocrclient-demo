# Preview 2 neutral local feed

**Unofficial evaluation packages. DO NOT use as shipped Microsoft packages.**

This folder contains exactly six architecture packages built from evaluated implementation commit
`6f7f3fa75d08599eb5005a0cd3db17d20694e1a8` at version
`10.8.0-preview2neutral.6f7f3fa`.

| Package ID | SHA-256 |
| --- | --- |
| `Microsoft.Extensions.DataIngestion` | `d2eba22420cef40edc18f49c1f35d561473c129005c2efc282bd1725d0ab4907` |
| `Microsoft.Extensions.DataIngestion.Abstractions` | `2f53db2c106eeed8f6139bd4549f4ffc6ba1cc5bdc24ca549bd03e1adc4e2ab3` |
| `Microsoft.Extensions.DataIngestion.DocumentExtraction` | `3506579e9e0c97673d80f945c9fb81c9bfed173308bddb61f0bf9fa26b51e9c6` |
| `Microsoft.Extensions.DocumentExtraction` | `78b2fe326e42f84d97daed443c2e85d79f4756c78b38e081d3adea8a546cfcea` |
| `Microsoft.Extensions.DocumentExtraction.Abstractions` | `2b863eb2f5d1751a44d739f85a33d15067625db0904058dd0614b786239bea65` |
| `Microsoft.Extensions.Documents.Abstractions` | `09db41f30dab97b5e94596761805d063a6cffb5baf4044a0ec24814cb9cf98fe` |

Every nuspec reports:

- repository: `https://github.com/luisquintanilla/extensions.git`
- branch: `luisquintanilla-neutral-document-tree`
- commit: `6f7f3fa75d08599eb5005a0cd3db17d20694e1a8`
- version: `10.8.0-preview2neutral.6f7f3fa`

`scripts/build-local-feed.sh` verifies exact package count, IDs, hashes, and nuspec provenance. Set
`SOURCE_FEED` to stage a replacement feed; the script validates the source before copying and rolls
back the committed feed on any target validation failure.

The architecture presentation commit
`7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2` is links/docs evidence only and is never a package
source. `provenance.json` records it separately from the evaluated implementation.

Published MEAI, vector, provider, and evaluation dependencies resolve from NuGet.org and are not
part of this closed six-package architecture feed.
