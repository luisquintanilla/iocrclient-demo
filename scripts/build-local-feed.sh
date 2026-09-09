#!/usr/bin/env bash
#
# Build or install the exact Preview 2 explicit-bridge feed.
#
# Evaluated source:
#   https://github.com/luisquintanilla/extensions.git
#   c1913907f05148370a84824b669d73249bb502e4
#
# By default, validate the committed six-package feed.
# PREBUILT_FEED installs the corrected architecture artifact.
# REBUILD_FROM_SOURCE=1 fetches and packs the immutable commit, then requires byte-identical hashes.

set -euo pipefail

EXTENSIONS_SHA="c1913907f05148370a84824b669d73249bb502e4"
COMMON_BASE_SHA="f6ba2df16275bfc5eaf50aeb9327e2ec34ee8129"
PREVIEW2_SHA="e124c123afeeda2f271f3b99a70eb3cfe187a471"
DEV_VERSION="10.8.0-preview2bridge.c191390"
REQUIRED_DOTNET_SDK="10.0.303"
FORK="https://github.com/luisquintanilla/extensions.git"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CACHE="${CACHE:-$HOME/dev/extensions-preview2-bridge-cache}"
TARGET_FEED="$ROOT/local-feed"
FEED="$TARGET_FEED"
STAGE=""
NUGET_PACKAGES="$ROOT/.nuget/feed-build-$$"
export NUGET_PACKAGES

ids=(
  Microsoft.Extensions.AI
  Microsoft.Extensions.AI.Abstractions
  Microsoft.Extensions.DataIngestion
  Microsoft.Extensions.DataIngestion.Abstractions
  Microsoft.Extensions.DataIngestion.DocumentExtraction
  Microsoft.Extensions.DocumentExtraction.Abstractions
)
hashes=(
  a8192d63fa45ad84cfb018107c8431290e1aee6f7cd8454c1fac4302c3f085ad
  13ec6febf70c77f7352e736b6e54e469706be435895271fb05fa0a91b6e3fecb
  569c315c3f8fc5d80140db53fb5f13046d6535967d61f4061f6029cbc73caa81
  06a201a6687b5abfb3e593f1557614e2071cba7cecdb2d3d5d2383459d61acff
  904f50db70912c45230e55c52446e3dc776d3a4eb79eaff11345c859d516f95a
  79dc4282564a82a2a21c6347d9a964d2e05be49308d11644e27a47152ae58c2c
)
projects=(
  src/Libraries/Microsoft.Extensions.AI/Microsoft.Extensions.AI.csproj
  src/Libraries/Microsoft.Extensions.AI.Abstractions/Microsoft.Extensions.AI.Abstractions.csproj
  src/Libraries/Microsoft.Extensions.DataIngestion/Microsoft.Extensions.DataIngestion.csproj
  src/Libraries/Microsoft.Extensions.DataIngestion.Abstractions/Microsoft.Extensions.DataIngestion.Abstractions.csproj
  src/Libraries/Microsoft.Extensions.DataIngestion.DocumentExtraction/Microsoft.Extensions.DataIngestion.DocumentExtraction.csproj
  src/Libraries/Microsoft.Extensions.DocumentExtraction.Abstractions/Microsoft.Extensions.DocumentExtraction.Abstractions.csproj
)

echo "==> feed target: $TARGET_FEED"
echo "==> source: $FORK @ $EXTENSIONS_SHA"
echo "==> version: $DEV_VERSION"
echo "==> isolated pack cache: $NUGET_PACKAGES"

if [ "$(dotnet --version)" != "$REQUIRED_DOTNET_SDK" ]; then
  echo "ERROR: expected .NET SDK $REQUIRED_DOTNET_SDK, found $(dotnet --version)" >&2
  exit 1
fi

mkdir -p "$TARGET_FEED"
for id in "${ids[@]}"; do
  rm -rf "$ROOT/.nuget/packages/${id,,}"
done

WORK=""
cleanup() {
  if [ -n "$WORK" ] && [ -d "$WORK" ]; then
    git -C "$CACHE" worktree remove --force "$WORK" >/dev/null 2>&1 || true
  fi
  rm -rf "$NUGET_PACKAGES"
  [ -z "$STAGE" ] || rm -rf "$STAGE"
}
trap cleanup EXIT

if [ -n "${PREBUILT_FEED:-}" ]; then
  echo "==> installing corrected architecture-session feed: $PREBUILT_FEED"
  if [ ! -d "$PREBUILT_FEED" ]; then
    echo "ERROR: PREBUILT_FEED does not exist: $PREBUILT_FEED" >&2
    exit 1
  fi
  STAGE="$ROOT/.nuget/feed-stage-$$"
  FEED="$STAGE"
  mkdir -p "$FEED"
  for id in "${ids[@]}"; do
    cp "$PREBUILT_FEED/$id.$DEV_VERSION.nupkg" "$FEED/"
  done
elif [ "${REBUILD_FROM_SOURCE:-0}" = "1" ]; then
  STAGE="$ROOT/.nuget/feed-stage-$$"
  FEED="$STAGE"
  mkdir -p "$FEED"
  if [ ! -d "$CACHE/.git" ]; then
    mkdir -p "$(dirname "$CACHE")"
    git clone --filter=blob:none --no-checkout "$FORK" "$CACHE"
  fi
  git -C "$CACHE" config core.longpaths true
  git -C "$CACHE" fetch --no-tags "$FORK" "$EXTENSIONS_SHA"
  WORK="$CACHE-worktree-$$"
  git -C "$CACHE" worktree add --detach "$WORK" "$EXTENSIONS_SHA"

  actual="$(git -C "$WORK" rev-parse HEAD)"
  [ "$actual" = "$EXTENSIONS_SHA" ] \
    || { echo "ERROR: expected $EXTENSIONS_SHA, checked out $actual" >&2; exit 1; }
  git -C "$WORK" merge-base --is-ancestor "$COMMON_BASE_SHA" "$EXTENSIONS_SHA" \
    || { echo "ERROR: corrected common base is not an ancestor" >&2; exit 1; }
  git -C "$WORK" merge-base --is-ancestor "$PREVIEW2_SHA" "$COMMON_BASE_SHA" \
    || { echo "ERROR: authoritative Preview 2 is not an ancestor" >&2; exit 1; }
  [ -z "$(git -C "$WORK" status --porcelain --untracked-files=all)" ] \
    || { echo "ERROR: source worktree is not clean" >&2; exit 1; }

  for project in "${projects[@]}"; do
    echo "==> pack $(basename "$project")"
    (cd "$ROOT" && dotnet pack "$WORK/$project" -c Release \
      -p:Version="$DEV_VERSION" -p:PackageVersion="$DEV_VERSION" \
      -p:RepositoryUrl="$FORK" -p:RepositoryCommit="$EXTENSIONS_SHA" \
      -p:DebugType=none -p:DebugSymbols=false -p:IncludeSymbols=false \
      -o "$FEED")
  done
else
  echo "==> validating committed corrected feed"
fi

count="$(find "$FEED" -maxdepth 1 -type f -iname '*.nupkg' | wc -l | tr -d ' ')"
[ "$count" = "${#ids[@]}" ] \
  || { echo "ERROR: expected ${#ids[@]} packages, found $count" >&2; exit 1; }

echo "==> verify six package hashes and nuspec provenance"
for i in "${!ids[@]}"; do
  id="${ids[$i]}"
  nupkg="$FEED/$id.$DEV_VERSION.nupkg"
  [ -f "$nupkg" ] || { echo "ERROR: missing $nupkg" >&2; exit 1; }
  actual_hash="$(sha256sum "$nupkg" | awk '{print $1}')"
  [ "$actual_hash" = "${hashes[$i]}" ] \
    || { echo "ERROR: hash mismatch for $id: $actual_hash" >&2; exit 1; }
  nuspec="$(unzip -p "$nupkg" '*.nuspec')"
  grep -Fq "<id>$id</id>" <<<"$nuspec" \
    || { echo "ERROR: ID mismatch in $id" >&2; exit 1; }
  grep -Fq "<version>$DEV_VERSION</version>" <<<"$nuspec" \
    || { echo "ERROR: version mismatch in $id" >&2; exit 1; }
  grep -Fq "url=\"$FORK\"" <<<"$nuspec" \
    || { echo "ERROR: repository mismatch in $id" >&2; exit 1; }
  grep -Fq "commit=\"$EXTENSIONS_SHA\"" <<<"$nuspec" \
    || { echo "ERROR: commit mismatch in $id" >&2; exit 1; }
  echo "    PASS $id ${hashes[$i]}"
done

if [ -n "$STAGE" ]; then
  echo "==> validated replacement; atomically replacing six package files"
  for id in "${ids[@]}"; do
    package="$id.$DEV_VERSION.nupkg"
    temporary="$TARGET_FEED/.$package.tmp-$$"
    cp "$STAGE/$package" "$temporary"
    mv -f "$temporary" "$TARGET_FEED/$package"
  done
  for file in "$TARGET_FEED"/*.nupkg; do
    keep=false
    for id in "${ids[@]}"; do
      if [ "$(basename "$file")" = "$id.$DEV_VERSION.nupkg" ]; then
        keep=true
        break
      fi
    done
    $keep || rm -f "$file"
  done
fi

echo "==> PASS exact Preview 2 bridge feed"
