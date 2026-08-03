#!/usr/bin/env bash
#
# build-local-feed.sh — reproduce the "virtual monorepo" local NuGet feed the samples run on.
#
# The samples don't run on a vendored copy of the DocumentExtraction API — they run on the REAL
# dotnet/extensions code, packed locally. This script builds one coherent feed = one source tree, so
# every Microsoft.Extensions.* assembly (AI(.Abstractions/.OpenAI), DataIngestion(.Abstractions),
# and the new DocumentExtraction(.Abstractions)) comes from the SAME tree with no version skew. The
# composition is:
#
#     data-ingestion-preview2 @ da091f9e               (latest MEDI: non-generic IngestionChunk +
#                                                       IngestionChunkVectorRecord)
#       + #7588  Microsoft.Extensions.DocumentExtraction(.Abstractions)
#                                                       (the extraction peer library; the two DE
#                                                        project folders grafted from the PR head)
#
# #7588 targets main and preview2 is far behind main, so a full merge would conflict. The
# DocumentExtraction libraries are self-contained project folders that depend only on
# Microsoft.Extensions.AI.Abstractions (present in the preview2 base), so we GRAFT the two project
# folders plus one const the [Experimental] attributes reference. Everything else (Azure SDKs,
# OpenAI) stays on nuget.org — the local feed only carries the locally-packed dev bits.
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
DE_ABS="src/Libraries/Microsoft.Extensions.DocumentExtraction.Abstractions"
DE="src/Libraries/Microsoft.Extensions.DocumentExtraction"

echo "==> feed target: $FEED  (version $DEV_VERSION)"

# --- 1) base: data-ingestion-preview2 -----------------------------------------------------------
# Clone extensions and land on data-ingestion-preview2 (the latest MEDI base). If $WORK already exists
# (the common case here), we assume it is already on data-ingestion-preview2 and skip re-fetching.
# Validated base commit: da091f9e (non-generic IngestionChunk + IngestionChunkVectorRecord).
if [ ! -d "$WORK/.git" ]; then
  echo "==> cloning dotnet/extensions -> $WORK"
  git clone "$UPSTREAM" "$WORK"
  git -C "$WORK" checkout data-ingestion-preview2
else
  echo "==> reusing existing tree at $WORK (assumed on data-ingestion-preview2)"
fi

# --- 2) graft #7588 DocumentExtraction (additive project folders) -------------------------------
# #7588 targets main and preview2 is far behind main, so a full merge would conflict. The
# DocumentExtraction libraries are self-contained project folders (they ProjectReference
# ..\Microsoft.Extensions.AI.Abstractions, which exists in the preview2 base), so we GRAFT the two
# folders whole. Clear any prior copy first so a re-run doesn't graft onto a stale tree.
#
# Two sources, same footprint:
#   * default  — the public PR head (pull/7588/head): reproducible from a public ref, no local branch.
#   * DE_SRC   — a LOCAL worktree (e.g. DE_SRC=$HOME/dev/ext-wt-figure): the pre-publish validation
#                path, for building the feed against local DocumentExtraction changes before the PR
#                head is updated.
rm -rf "$WORK/$DE_ABS" "$WORK/$DE"
if [ -n "${DE_SRC:-}" ]; then
  echo "==> grafting DocumentExtraction project folders from local DE_SRC=$DE_SRC"
  cp -rT "$DE_SRC/$DE_ABS" "$WORK/$DE_ABS"
  cp -rT "$DE_SRC/$DE" "$WORK/$DE"
  # drop any build output that rode along from the source worktree
  rm -rf "$WORK/$DE_ABS/bin" "$WORK/$DE_ABS/obj" "$WORK/$DE/bin" "$WORK/$DE/obj"
else
  echo "==> fetching #7588 head (pull/7588/head)"
  git -C "$WORK" fetch "$UPSTREAM" pull/7588/head:pr-7588
  echo "==> grafting DocumentExtraction project folders from pr-7588 (#7588 head)"
  git -C "$WORK" checkout pr-7588 -- "$DE_ABS" "$DE"
fi

# One const the grafted [Experimental] attributes reference. The reshape source defines it via a
# two-level indirection (DocumentExtraction = DocumentExtractionExperiments = "MEDE0001"); the graft
# only needs the resolved value, so we inject the single sufficient constant.
if ! grep -q "DocumentExtraction" "$WORK/src/Shared/DiagnosticIds/DiagnosticIds.cs"; then
  echo "    + DiagnosticIds.Experiments.DocumentExtraction = MEDE0001"
  sed -i 's/\(internal const string AIOpenAIRequestPolicies = AIExperiments;\)/\1\n        internal const string DocumentExtraction = "MEDE0001";/' \
    "$WORK/src/Shared/DiagnosticIds/DiagnosticIds.cs"
fi

# --- 3) clear stale cache (same dev version does NOT refresh in ~/.nuget) -----------------------
echo "==> clearing stale $DEV_VERSION from the global NuGet cache"
for pkg in microsoft.extensions.ai microsoft.extensions.ai.abstractions microsoft.extensions.ai.openai \
           microsoft.extensions.dataingestion microsoft.extensions.dataingestion.abstractions \
           microsoft.extensions.ai.evaluation microsoft.extensions.ai.evaluation.quality \
           microsoft.extensions.ai.evaluation.reporting microsoft.extensions.ai.evaluation.nlp \
           microsoft.extensions.documentextraction microsoft.extensions.documentextraction.abstractions; do
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
  "$DE_ABS/Microsoft.Extensions.DocumentExtraction.Abstractions.csproj"
  "$DE/Microsoft.Extensions.DocumentExtraction.csproj"
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
