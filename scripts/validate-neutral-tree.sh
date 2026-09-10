#!/usr/bin/env bash

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STATE="$(mktemp -d "${TMPDIR:-/tmp}/preview2-neutral-consumer.XXXXXX")"

cleanup() {
  rm -rf "$STATE"
}
trap cleanup EXIT

export NUGET_PACKAGES="$STATE/nuget-packages"
export DOTNET_CLI_HOME="$STATE/dotnet-home"
export NUGET_HTTP_CACHE_PATH="$STATE/nuget-http-cache"
export NUGET_PLUGINS_CACHE_PATH="$STATE/nuget-plugins-cache"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1

restore() {
  dotnet restore "$1" --configfile "$ROOT/nuget.config" --no-cache --force
}

"$ROOT/scripts/build-local-feed.sh"

restore "$ROOT/samples/ocr-shape/OcrShape.csproj"
dotnet build "$ROOT/samples/ocr-shape/OcrShape.csproj" --no-restore --nologo

restore "$ROOT/samples/bench/OcrBench/OcrBench.csproj"
dotnet build "$ROOT/samples/bench/OcrBench/OcrBench.csproj" --no-restore --nologo

sample_count=0
for sample in "$ROOT"/samples/*.cs; do
  restore "$sample"
  dotnet build "$sample" --no-restore --nologo --verbosity quiet
  sample_count=$((sample_count + 1))
done
if [ "$sample_count" -ne 17 ]; then
  echo "ERROR: expected 17 file-based samples, found $sample_count" >&2
  exit 1
fi

restore "$ROOT/samples/documents-only/DocumentsOnly.csproj"
dotnet build "$ROOT/samples/documents-only/DocumentsOnly.csproj" --no-restore --nologo

restore "$ROOT/hero/hero.sln"
dotnet build "$ROOT/hero/hero.sln" --no-restore --nologo

( cd "$ROOT/samples" && dotnet run 17-neutral-shared-tree-validation.cs --no-restore --no-build > "$STATE/neutral.txt" )
diff -u --strip-trailing-cr "$ROOT/samples/output/17-neutral-shared-tree-validation.txt" "$STATE/neutral.txt"

( cd "$ROOT" && dotnet run --project samples/documents-only/DocumentsOnly.csproj --no-restore --no-build > "$STATE/documents-only.txt" )
diff -u --strip-trailing-cr "$ROOT/samples/output/documents-only.txt" "$STATE/documents-only.txt"

( cd "$ROOT" && npm ci --no-audit --no-fund --loglevel=error )
( cd "$ROOT" && npm run build )

echo "PASS: Preview 2 neutral feed, clean restores, 17 samples, providers, hero, proofs, and site"
