# `local-feed/`: Preview 2 bridge packages

**These are unofficial local builds for architecture validation.** Do not use them as shipped
Microsoft packages.

## Immutable source

- architecture: explicit Document Extraction-to-Preview 2 MEDI bridge
- architecture PR presentation head:
  [`a1eb56c4c497738ef06c557f63cdc071082a4536`](https://github.com/luisquintanilla/extensions/commit/a1eb56c4c497738ef06c557f63cdc071082a4536)
- evaluated package source:
  [`c1913907f05148370a84824b669d73249bb502e4`](https://github.com/luisquintanilla/extensions/commit/c1913907f05148370a84824b669d73249bb502e4)
- corrected common base: `f6ba2df16275bfc5eaf50aeb9327e2ec34ee8129`
- authoritative Preview 2 ancestor: `e124c123afeeda2f271f3b99a70eb3cfe187a471`
- package version: `10.8.0-preview2bridge.c191390`
- repository in every nuspec: `https://github.com/luisquintanilla/extensions.git`

Do not repin packages to the later presentation head. It includes architecture documentation after
the evaluated code.

## Six-package feed

| Package | SHA-256 |
| --- | --- |
| `Microsoft.Extensions.AI` | `a8192d63fa45ad84cfb018107c8431290e1aee6f7cd8454c1fac4302c3f085ad` |
| `Microsoft.Extensions.AI.Abstractions` | `13ec6febf70c77f7352e736b6e54e469706be435895271fb05fa0a91b6e3fecb` |
| `Microsoft.Extensions.DataIngestion` | `569c315c3f8fc5d80140db53fb5f13046d6535967d61f4061f6029cbc73caa81` |
| `Microsoft.Extensions.DataIngestion.Abstractions` | `06a201a6687b5abfb3e593f1557614e2071cba7cecdb2d3d5d2383459d61acff` |
| `Microsoft.Extensions.DataIngestion.DocumentExtraction` | `904f50db70912c45230e55c52446e3dc776d3a4eb79eaff11345c859d516f95a` |
| `Microsoft.Extensions.DocumentExtraction.Abstractions` | `79dc4282564a82a2a21c6347d9a964d2e05be49308d11644e27a47152ae58c2c` |

These six runtime packages are committed so a clone restores the exact evaluated bytes. Symbol
packages remain gitignored. To replace them from the corrected architecture-session artifact:

```bash
PREBUILT_FEED=/path/to/preview2-feed ../scripts/build-local-feed.sh
```

Set `REBUILD_FROM_SOURCE=1` to reconstruct from the immutable commit. The default validation, artifact
install, and explicit rebuild all fail unless exactly six packages match every hash, ID, version,
repository, and commit. Validated replacements use same-directory atomic file renames, so the
existing feed remains available. A byte-different rebuilt archive is not silently accepted.

External provider SDKs, vector providers, and evaluation helpers resolve from nuget.org. The six
architecture packages resolve from this feed at unique prerelease versions. Restore caches live under
the repository through `nuget.config`.

## License

The packaged code is from `dotnet/extensions`, licensed under the
[MIT License](https://github.com/dotnet/extensions/blob/main/LICENSE). Each package retains its
license metadata.
