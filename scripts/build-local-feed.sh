#!/usr/bin/env bash
#
# build-local-feed.sh — reproduce the "virtual monorepo" local NuGet feed the samples run on.
#
# The samples don't run on a vendored copy of IOcrClient — they run on the REAL dotnet/extensions
# code, packed locally. This script builds one coherent feed = one source tree, so every
# Microsoft.Extensions.* assembly (Abstractions, AI, AI.OpenAI, DataIngestion) comes from the SAME
# commit with no version skew. That commit is:
#
#     data-ingestion-preview2                          (latest MEDI: non-generic IngestionChunk)
#       + #7588  IOcrClient                            (the OCR seam; grafted from the PR head)
#
# #7588 targets main and preview2 is far behind main, so a full merge would conflict. Instead we
# GRAFT the additive Ocr/ folders (they are almost all new files) plus two one-line const edits.
# Everything else (Azure SDKs, OpenAI) stays on nuget.org — the local feed only carries the
# locally-packed dev bits.
#
# Once #7588 ships, delete the local feed + nuget.config <clear/> and bump the samples to
# the published package versions. This whole script becomes unnecessary.
#
# Usage:
#     scripts/build-local-feed.sh
#
# Requires: git, and the pinned repo SDK the extensions clone provides (via its global.json).

set -euo pipefail

# --- knobs -------------------------------------------------------------------------------------
DEV_VERSION="${DEV_VERSION:-10.8.0-dev}"
WORK="${WORK:-$HOME/dev/extensions-preview2}"          # where the grafted extensions tree lives
FEED="$(cd "$(dirname "$0")/.." && pwd)/local-feed"    # <repo>/local-feed
UPSTREAM="https://github.com/dotnet/extensions.git"

ABS="src/Libraries/Microsoft.Extensions.AI.Abstractions"
AI="src/Libraries/Microsoft.Extensions.AI"

echo "==> feed target: $FEED  (version $DEV_VERSION)"

# --- 1) base: data-ingestion-preview2 -----------------------------------------------------------
# Clone extensions and land on data-ingestion-preview2 (the latest MEDI base). If $WORK already exists
# (the common case here), we assume it is already on data-ingestion-preview2 and skip re-fetching.
if [ ! -d "$WORK/.git" ]; then
  echo "==> cloning dotnet/extensions -> $WORK"
  git clone "$UPSTREAM" "$WORK"
  git -C "$WORK" checkout data-ingestion-preview2
else
  echo "==> reusing existing tree at $WORK (assumed on data-ingestion-preview2)"
fi

# --- 2) graft #7588 IOcrClient (additive) -------------------------------------------------------
# #7588 targets main and preview2 is far behind main, so a full merge would conflict. The Ocr/ folders
# are almost all new files, so we GRAFT them (copying only the two Ocr/ folders). Clear any prior copy
# first so a re-run doesn't nest Ocr/ inside Ocr/.
#
# Two sources, same footprint:
#   * default  — the public PR head (pull/7588/head): reproducible from a public ref, no local branch.
#   * OCR_SRC  — a LOCAL worktree (e.g. OCR_SRC=/path/to/extensions-ocr-worktree): the pre-publish
#                validation path, for building the feed against local Ocr/ changes before the PR head
#                is updated.
rm -rf "$WORK/$ABS/Ocr" "$WORK/$AI/Ocr"
if [ -n "${OCR_SRC:-}" ]; then
  echo "==> grafting additive Ocr/ folders from local OCR_SRC=$OCR_SRC"
  cp -rT "$OCR_SRC/$ABS/Ocr" "$WORK/$ABS/Ocr"
  cp -rT "$OCR_SRC/$AI/Ocr" "$WORK/$AI/Ocr"
else
  echo "==> fetching #7588 head (pull/7588/head)"
  git -C "$WORK" fetch "$UPSTREAM" pull/7588/head:pr-7588
  echo "==> grafting additive Ocr/ folders from pr-7588 (#7588 head)"
  git -C "$WORK" checkout pr-7588 -- "$ABS/Ocr" "$AI/Ocr"
fi

# Two one-line edits the grafted files depend on:
#   a) the experimental diagnostic id the [Experimental] attributes reference
if ! grep -q "AIOcr" "$WORK/src/Shared/DiagnosticIds/DiagnosticIds.cs"; then
  echo "    + DiagnosticIds.Experiments.AIOcr"
  sed -i 's/\(internal const string AIOpenAIRequestPolicies = AIExperiments;\)/\1\n        internal const string AIOcr = AIExperiments;/' \
    "$WORK/src/Shared/DiagnosticIds/DiagnosticIds.cs"
fi
#   b) the OpenTelemetry const OpenTelemetryOcrClient emits
if ! grep -q "PagesProcessed" "$WORK/$AI/OpenTelemetryConsts.cs"; then
  echo "    + OpenTelemetryConsts.PagesProcessed"
  sed -i 's#\(public const string InputTokens = .*;\)#\1\n            public const string PagesProcessed = "gen_ai.usage.pages_processed"; // Non-standard#' \
    "$WORK/$AI/OpenTelemetryConsts.cs"
fi

# --- 3) clear stale cache (same dev version does NOT refresh in ~/.nuget) -----------------------
echo "==> clearing stale $DEV_VERSION from the global NuGet cache"
for pkg in microsoft.extensions.ai microsoft.extensions.ai.abstractions microsoft.extensions.ai.openai \
           microsoft.extensions.dataingestion microsoft.extensions.dataingestion.abstractions \
           microsoft.extensions.ai.evaluation microsoft.extensions.ai.evaluation.quality \
           microsoft.extensions.ai.evaluation.reporting microsoft.extensions.ai.evaluation.nlp; do
  rm -rf "$HOME/.nuget/packages/$pkg/$DEV_VERSION"
done

# --- 4) pack the coherent set -> local-feed ----------------------------------------------------
# DebugType=none (no PDB) keeps the local build path out of the shipped DLLs — otherwise a
# non-deterministic Release build embeds the absolute .pdb path (i.e. the packer's home dir) into
# each assembly's debug directory. No symbols package either; the local feed doesn't need one.
DOTNET="$WORK/.dotnet/dotnet"; [ -x "$DOTNET" ] || DOTNET="dotnet"   # prefer the repo-pinned SDK
mkdir -p "$FEED"
projects=(
  "$ABS/Microsoft.Extensions.AI.Abstractions.csproj"
  "$AI/Microsoft.Extensions.AI.csproj"
  "src/Libraries/Microsoft.Extensions.AI.OpenAI/Microsoft.Extensions.AI.OpenAI.csproj"
  "src/Libraries/Microsoft.Extensions.DataIngestion/Microsoft.Extensions.DataIngestion.csproj"
  "src/Libraries/Microsoft.Extensions.DataIngestion.Abstractions/Microsoft.Extensions.DataIngestion.Abstractions.csproj"
  "src/Libraries/Microsoft.Extensions.AI.Evaluation/Microsoft.Extensions.AI.Evaluation.csproj"
  "src/Libraries/Microsoft.Extensions.AI.Evaluation.Quality/Microsoft.Extensions.AI.Evaluation.Quality.csproj"
  "src/Libraries/Microsoft.Extensions.AI.Evaluation.Reporting/CSharp/Microsoft.Extensions.AI.Evaluation.Reporting.csproj"
  "src/Libraries/Microsoft.Extensions.AI.Evaluation.NLP/Microsoft.Extensions.AI.Evaluation.NLP.csproj"
)
for proj in "${projects[@]}"; do
  echo "==> pack $(basename "$proj")"
  ( cd "$WORK" && "$DOTNET" pack "$proj" -c Release \
      -p:Version="$DEV_VERSION" -p:PackageVersion="$DEV_VERSION" \
      -p:DebugType=none -p:DebugSymbols=false -p:IncludeSymbols=false \
      -o "$FEED" )
done

echo
echo "==> done. $FEED now carries:"
ls "$FEED"/*.nupkg | sed 's#.*/#    #'
echo "    Samples resolve these via nuget.config (local-feed). Azure SDKs come from nuget.org."
