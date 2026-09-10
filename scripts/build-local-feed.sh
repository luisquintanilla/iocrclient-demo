#!/usr/bin/env bash

set -euo pipefail

IMPLEMENTATION_SHA="704a3e44ef4d7b053748780549fc2c8e929a444b"
PRESENTATION_SHA="7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2"
COMMON_BASE_SHA="f6ba2df16275bfc5eaf50aeb9327e2ec34ee8129"
PREVIEW2_ANCESTOR_SHA="e124c123afeeda2f271f3b99a70eb3cfe187a471"
PACKAGE_VERSION="10.8.0-preview2neutral.704a3e4"
REPOSITORY_URL="https://github.com/dotnet/extensions.git"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FEED="$ROOT/local-feed"
STATE="$(mktemp -d "${TMPDIR:-/tmp}/preview2-neutral-feed.XXXXXX")"
BACKUP="$STATE/backup"

cleanup() {
  rm -rf "$STATE"
}
trap cleanup EXIT

if [ -n "${PYTHON:-}" ]; then
  CANDIDATES=("$PYTHON")
else
  CANDIDATES=(python3 python)
fi
PYTHON3=""
for candidate in "${CANDIDATES[@]}"; do
  if command -v "$candidate" > /dev/null 2>&1 &&
      "$candidate" -c 'import sys; raise SystemExit(0 if sys.version_info.major == 3 else 1)'; then
    PYTHON3="$candidate"
    break
  fi
done
if [ -z "$PYTHON3" ]; then
  echo "ERROR: Python 3 is required to inspect NuGet package provenance." >&2
  exit 1
fi

verify_feed() {
  local feed="$1"
  "$PYTHON3" - "$feed" "$IMPLEMENTATION_SHA" "$PACKAGE_VERSION" "$REPOSITORY_URL" <<'PY'
import hashlib
import pathlib
import sys
import xml.etree.ElementTree as ET
import zipfile

feed = pathlib.Path(sys.argv[1])
expected_commit = sys.argv[2]
expected_version = sys.argv[3]
expected_repository = sys.argv[4]
expected = {
    "Microsoft.Extensions.DataIngestion": "2b6002fc142dace6a5b08a1bc845eb544d08523c4f75d60c6384a36255e8f7b0",
    "Microsoft.Extensions.DataIngestion.Abstractions": "6b8a88bb5f52121b05022c834de890669f8a8327a54bafa148df063675cf2f4f",
    "Microsoft.Extensions.DataIngestion.DocumentExtraction": "c2dd354bf6460b5f1f8b01186b5ff3f0c27ce790a6bb08535e30846250ca5d35",
    "Microsoft.Extensions.DocumentExtraction": "fa54be131cc99b3c870ea9789cde03584967413302e2fa7ac53f9ac6e89b79a1",
    "Microsoft.Extensions.DocumentExtraction.Abstractions": "a4347cb50702c82127af83cbcb5852d3148a2429f7920f13b89c0538b67e2b65",
    "Microsoft.Extensions.Documents.Abstractions": "c94ea97233f9756009012f8f25234974f56c950982d7021b2df22430d4c98f4b",
}
manifest_path = feed / "SHA256SUMS"
if not manifest_path.is_file():
    raise SystemExit(f"ERROR: missing SHA256SUMS in {feed}")
manifest = {}
for line in manifest_path.read_text(encoding="utf-8").splitlines():
    parts = line.split(maxsplit=1)
    if len(parts) != 2:
        raise SystemExit(f"ERROR: malformed SHA256SUMS line: {line}")
    digest, filename = parts
    manifest[filename.lstrip("*")] = digest.lower()
expected_files = {
    f"{package_id}.{expected_version}.nupkg": digest
    for package_id, digest in expected.items()
}
if manifest != expected_files:
    raise SystemExit("ERROR: SHA256SUMS does not exactly match the authoritative six packages")

packages = sorted(feed.glob("*.nupkg"))
if len(packages) != len(expected):
    raise SystemExit(f"ERROR: expected exactly {len(expected)} packages, found {len(packages)} in {feed}")

seen = set()
for package in packages:
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    with zipfile.ZipFile(package) as archive:
        nuspecs = [entry for entry in archive.namelist() if entry.lower().endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise SystemExit(f"ERROR: expected one nuspec in {package.name}")
        root = ET.fromstring(archive.read(nuspecs[0]))
    namespace = {"n": root.tag.partition("}")[0].lstrip("{")} if "}" in root.tag else {}
    prefix = "n:" if namespace else ""
    metadata = root.find(f"{prefix}metadata", namespace)
    package_id = metadata.find(f"{prefix}id", namespace).text
    version = metadata.find(f"{prefix}version", namespace).text
    repository = metadata.find(f"{prefix}repository", namespace)
    repository_url = repository.attrib.get("url")
    commit = repository.attrib.get("commit")
    if package_id not in expected:
        raise SystemExit(f"ERROR: unexpected package ID {package_id}")
    if package_id in seen:
        raise SystemExit(f"ERROR: duplicate package ID {package_id}")
    if digest != expected[package_id]:
        raise SystemExit(f"ERROR: SHA-256 mismatch for {package_id}: {digest}")
    if version != expected_version or repository_url != expected_repository or commit != expected_commit:
        raise SystemExit(
            f"ERROR: nuspec provenance mismatch for {package_id}: "
            f"version={version} repo={repository_url} commit={commit}")
    seen.add(package_id)
    print(f"{package_id} | {version} | {repository_url} | {commit} | {digest}")

if seen != set(expected):
    raise SystemExit(f"ERROR: missing package IDs: {sorted(set(expected) - seen)}")
PY
}

echo "Preview 2 neutral implementation: $IMPLEMENTATION_SHA"
echo "Presentation evidence only: $PRESENTATION_SHA"
echo "Common base: $COMMON_BASE_SHA"
echo "Preview 2 ancestor: $PREVIEW2_ANCESTOR_SHA"

if [ -n "${SOURCE_FEED:-}" ]; then
  SOURCE_FEED="$(cd "$SOURCE_FEED" && pwd)"
  echo "Validating replacement source feed: $SOURCE_FEED"
  verify_feed "$SOURCE_FEED"
  mkdir -p "$BACKUP"
  cp "$FEED"/*.nupkg "$FEED/SHA256SUMS" "$BACKUP/"
  rollback() {
    rm -f "$FEED"/*.nupkg "$FEED/SHA256SUMS"
    cp "$BACKUP"/* "$FEED/"
  }
  trap 'rollback; cleanup' ERR
  rm -f "$FEED"/*.nupkg "$FEED/SHA256SUMS"
  cp "$SOURCE_FEED"/*.nupkg "$FEED/"
  ( cd "$FEED" && sha256sum *.nupkg | LC_ALL=C sort -k2 > SHA256SUMS )
  verify_feed "$FEED"
  trap cleanup EXIT
  echo "Replacement feed committed in place after complete validation."
else
  verify_feed "$FEED"
fi

echo "PASS: exact six-package Preview 2 neutral feed verified"
