#!/usr/bin/env bash
set -euo pipefail

# Use the same guarded recipe on Windows and hosts with PowerShell 7.
# Pass -SourceManifestPath, -Source, -Destination and a NEW -Version.
script_dir="$(cd "$(dirname "$0")" && pwd)"
command -v pwsh >/dev/null || { echo "PowerShell 7 (pwsh) is required." >&2; exit 1; }
exec pwsh -NoProfile -File "$script_dir/Build-E2EFeed.ps1" "$@"
