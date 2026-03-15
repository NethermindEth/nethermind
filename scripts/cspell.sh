#!/usr/bin/env bash
# Runs cspell on the given files using the repo's cspell.json config.
# Used by local hooks as the local equivalent of the cspell-action CI step.
# Both this script and the CI action read the same cspell.json, so any
# word/rule change applies to both automatically.
#
# Usage:
#   bash scripts/cspell.sh path/to/file.cs [path/to/other.cs ...]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

exec npx cspell --no-progress --config "$REPO_ROOT/cspell.json" "$@"
