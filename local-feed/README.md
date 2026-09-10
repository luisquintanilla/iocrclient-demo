# Preview 2 neutral local feed

**Unofficial evaluation packages. DO NOT use as shipped Microsoft packages.**

This folder contains exactly six architecture packages built from evaluated implementation commit
`704a3e44ef4d7b053748780549fc2c8e929a444b` at version
`10.8.0-preview2neutral.704a3e4`.

| Package ID | SHA-256 |
| --- | --- |
| `Microsoft.Extensions.DataIngestion` | `2b6002fc142dace6a5b08a1bc845eb544d08523c4f75d60c6384a36255e8f7b0` |
| `Microsoft.Extensions.DataIngestion.Abstractions` | `6b8a88bb5f52121b05022c834de890669f8a8327a54bafa148df063675cf2f4f` |
| `Microsoft.Extensions.DataIngestion.DocumentExtraction` | `c2dd354bf6460b5f1f8b01186b5ff3f0c27ce790a6bb08535e30846250ca5d35` |
| `Microsoft.Extensions.DocumentExtraction` | `fa54be131cc99b3c870ea9789cde03584967413302e2fa7ac53f9ac6e89b79a1` |
| `Microsoft.Extensions.DocumentExtraction.Abstractions` | `a4347cb50702c82127af83cbcb5852d3148a2429f7920f13b89c0538b67e2b65` |
| `Microsoft.Extensions.Documents.Abstractions` | `c94ea97233f9756009012f8f25234974f56c950982d7021b2df22430d4c98f4b` |

Every nuspec reports:

- repository: `https://github.com/dotnet/extensions.git`
- commit: `704a3e44ef4d7b053748780549fc2c8e929a444b`
- version: `10.8.0-preview2neutral.704a3e4`

`scripts/build-local-feed.sh` verifies exact package count, IDs, hashes, and nuspec provenance. Set
`SOURCE_FEED` to stage a replacement feed; the script validates the source before copying and rolls
back the committed feed on any target validation failure.

The architecture presentation commit
`7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2` is links/docs evidence only and is never a package
source. `provenance.json` records it separately from the evaluated implementation.

Published MEAI, vector, provider, and evaluation dependencies resolve from NuGet.org and are not
part of this closed six-package architecture feed.
