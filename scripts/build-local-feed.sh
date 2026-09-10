#!/usr/bin/env bash

set -euo pipefail

IMPLEMENTATION_SHA="6f7f3fa75d08599eb5005a0cd3db17d20694e1a8"
PRESENTATION_SHA="7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2"
COMMON_BASE_SHA="f6ba2df16275bfc5eaf50aeb9327e2ec34ee8129"
PREVIEW2_ANCESTOR_SHA="e124c123afeeda2f271f3b99a70eb3cfe187a471"
PACKAGE_VERSION="10.8.0-preview2neutral.6f7f3fa"
REPOSITORY_URL="https://github.com/luisquintanilla/extensions.git"
REPOSITORY_BRANCH="refs/heads/luisquintanilla-neutral-document-tree"
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
  "$PYTHON3" - "$feed" "$IMPLEMENTATION_SHA" "$PACKAGE_VERSION" "$REPOSITORY_URL" "$REPOSITORY_BRANCH" <<'PY'
import hashlib
import pathlib
import sys
import xml.etree.ElementTree as ET
import zipfile

feed = pathlib.Path(sys.argv[1])
expected_commit = sys.argv[2]
expected_version = sys.argv[3]
expected_repository = sys.argv[4]
expected_branch = sys.argv[5]
expected = {
    "Microsoft.Extensions.DataIngestion": "d2eba22420cef40edc18f49c1f35d561473c129005c2efc282bd1725d0ab4907",
    "Microsoft.Extensions.DataIngestion.Abstractions": "2f53db2c106eeed8f6139bd4549f4ffc6ba1cc5bdc24ca549bd03e1adc4e2ab3",
    "Microsoft.Extensions.DataIngestion.DocumentExtraction": "3506579e9e0c97673d80f945c9fb81c9bfed173308bddb61f0bf9fa26b51e9c6",
    "Microsoft.Extensions.DocumentExtraction": "78b2fe326e42f84d97daed443c2e85d79f4756c78b38e081d3adea8a546cfcea",
    "Microsoft.Extensions.DocumentExtraction.Abstractions": "2b863eb2f5d1751a44d739f85a33d15067625db0904058dd0614b786239bea65",
    "Microsoft.Extensions.Documents.Abstractions": "09db41f30dab97b5e94596761805d063a6cffb5baf4044a0ec24814cb9cf98fe",
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
    branch = repository.attrib.get("branch")
    commit = repository.attrib.get("commit")
    if package_id not in expected:
        raise SystemExit(f"ERROR: unexpected package ID {package_id}")
    if package_id in seen:
        raise SystemExit(f"ERROR: duplicate package ID {package_id}")
    if digest != expected[package_id]:
        raise SystemExit(f"ERROR: SHA-256 mismatch for {package_id}: {digest}")
    if (version != expected_version or repository_url != expected_repository
            or branch != expected_branch or commit != expected_commit):
        raise SystemExit(
            f"ERROR: nuspec provenance mismatch for {package_id}: "
            f"version={version} repo={repository_url} branch={branch} commit={commit}")
    seen.add(package_id)
    print(f"{package_id} | {version} | {repository_url} | {branch} | {commit} | {digest}")

if seen != set(expected):
    raise SystemExit(f"ERROR: missing package IDs: {sorted(set(expected) - seen)}")
PY
}

echo "Preview 2 neutral implementation: $IMPLEMENTATION_SHA"
echo "Presentation evidence only: $PRESENTATION_SHA"
echo "Common base: $COMMON_BASE_SHA"
echo "Preview 2 ancestor: $PREVIEW2_ANCESTOR_SHA"
echo "Package branch: $REPOSITORY_BRANCH"

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
