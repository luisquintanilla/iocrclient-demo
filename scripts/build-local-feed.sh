#!/usr/bin/env bash
#
# Build the exact provisional Documents + DocumentExtraction + MEDI bridge feed used by the
# consumer proof. The packages are rebuilt from source; no fake public package version is used.
#
# Usage:
#   WORK="$HOME/dev/extensions-n-g4" scripts/build-local-feed.sh
#
# WORK is outside this repository by default. The script never edits the extensions repository
# checkout used as its source: it clones or fetches into the separate WORK directory.

set -euo pipefail

DEV_VERSION="${DEV_VERSION:-10.10.0-dev}"
WORK="${WORK:-$HOME/dev/extensions-n-g4}"
FEED="${FEED:-$(cd "$(dirname "$0")/.." && pwd)/local-feed}"
UPSTREAM="${UPSTREAM:-https://github.com/dotnet/extensions.git}"
STACK_COMMIT="8ae3d2436d105c8449a712d58618ff444b617f7b"

echo "==> source: dotnet/extensions @ $STACK_COMMIT"
echo "==> feed:   $FEED ($DEV_VERSION)"

if [ ! -d "$WORK/.git" ]; then
  git clone "$UPSTREAM" "$WORK"
fi
git -C "$WORK" fetch --no-tags "$UPSTREAM" "$STACK_COMMIT"
git -C "$WORK" checkout --detach "$STACK_COMMIT"

# A single checkout is important: 8ae3d24 is the composition bridge commit whose ancestry is
# exactly 2d28dfd (extraction), then d35d5cb (Documents). AI, MEDI, and evaluation projects all
# come from the same source snapshot, so assembly identities cannot drift.
projects=(
  src/Libraries/Microsoft.Extensions.AI.Abstractions/Microsoft.Extensions.AI.Abstractions.csproj
  src/Libraries/Microsoft.Extensions.AI/Microsoft.Extensions.AI.csproj
  src/Libraries/Microsoft.Extensions.AI.OpenAI/Microsoft.Extensions.AI.OpenAI.csproj
  src/Libraries/Microsoft.Extensions.DataIngestion.Abstractions/Microsoft.Extensions.DataIngestion.Abstractions.csproj
  src/Libraries/Microsoft.Extensions.DataIngestion/Microsoft.Extensions.DataIngestion.csproj
  src/Libraries/Microsoft.Extensions.Documents.Abstractions/Microsoft.Extensions.Documents.Abstractions.csproj
  src/Libraries/Microsoft.Extensions.DocumentExtraction.Abstractions/Microsoft.Extensions.DocumentExtraction.Abstractions.csproj
  src/Libraries/Microsoft.Extensions.DocumentExtraction/Microsoft.Extensions.DocumentExtraction.csproj
  src/Libraries/Microsoft.Extensions.DataIngestion.DocumentExtraction/Microsoft.Extensions.DataIngestion.DocumentExtraction.csproj
  src/Libraries/Microsoft.Extensions.AI.Evaluation/Microsoft.Extensions.AI.Evaluation.csproj
  src/Libraries/Microsoft.Extensions.AI.Evaluation.Quality/Microsoft.Extensions.AI.Evaluation.Quality.csproj
  src/Libraries/Microsoft.Extensions.AI.Evaluation.Reporting/CSharp/Microsoft.Extensions.AI.Evaluation.Reporting.csproj
  src/Libraries/Microsoft.Extensions.AI.Evaluation.NLP/Microsoft.Extensions.AI.Evaluation.NLP.csproj
)

mkdir -p "$FEED"
DOTNET="${DOTNET:-dotnet}"
for project in "${projects[@]}"; do
  echo "==> pack $(basename "$project")"
  (
    cd "$WORK"
    "$DOTNET" restore "$project" \
      -p:TargetFrameworks=net10.0 \
      -p:NuGetAudit=false \
      --ignore-failed-sources
    "$DOTNET" pack "$project" -c Release --no-restore \
      -p:TargetFrameworks=net10.0 \
      -p:Version="$DEV_VERSION" \
      -p:PackageVersion="$DEV_VERSION" \
      -p:EnablePackageValidation=false \
      -p:DisableMinimumSupportedTfmCompatErrors=true \
      -p:RepositoryUrl="$UPSTREAM" \
      -p:RepositoryType=git \
      -p:PackageProjectUrl="$UPSTREAM" \
      -p:PackageLicenseExpression=MIT \
      -p:DebugType=none -p:DebugSymbols=false -p:IncludeSymbols=false \
      -o "$FEED"
  )
done

echo "==> feed contents"
find "$FEED" -maxdepth 1 -name '*.nupkg' -printf '    %f\n' | sort
