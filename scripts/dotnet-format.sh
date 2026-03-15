#!/usr/bin/env bash
# Runs dotnet format whitespace on src/Nethermind/ and tools/.
# All arguments are forwarded verbatim to dotnet format whitespace.
#
# CI usage (verify only, no changes written):
#   bash scripts/dotnet-format.sh --verify-no-changes
#
# Local/hook usage (auto-fix a single file):
#   bash scripts/dotnet-format.sh --include path/to/File.cs

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

dotnet format whitespace "$REPO_ROOT/src/Nethermind/" --folder "$@"
dotnet format whitespace "$REPO_ROOT/tools/" --folder "$@"
