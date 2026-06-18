#!/usr/bin/env bash
# sync-core.sh — copies src/Keylight/**/*.cs into unity/dev.keylight.sdk/Runtime/Core/
# preserving the subdirectory structure (Crypto/, Json/, and root-level files).
#
# Run from the repo root or from unity/:
#   ./unity/sync-core.sh
#
# Runtime/Core/ is generated; the single source of truth is src/Keylight/.
# Do not hand-edit files under Runtime/Core/ — edit src/ instead, then re-sync.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
SRC="$REPO_ROOT/src/Keylight"
DST="$SCRIPT_DIR/dev.keylight.sdk/Runtime/Core"

echo "Syncing $SRC -> $DST"

# Wipe and recreate so deleted files don't linger
rm -rf "$DST"
mkdir -p "$DST"

# rsync: source tree, .cs files only, preserve relative paths
# Exclude generated obj/ subdirectories explicitly before the include rules.
rsync -a \
  --exclude="obj/" \
  --include="*/" \
  --include="*.cs" \
  --exclude="*" \
  "$SRC/" "$DST/"

echo "Sync complete."
find "$DST" -name "*.cs" | sort
