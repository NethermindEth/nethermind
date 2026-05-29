#!/usr/bin/env bash
# Sync canonical agent skills into tool-specific directories.
#
# `.agents/skills/` is the single source of truth. Claude Code reads
# `.claude/skills/` and Cursor reads `.cursor/skills/`; neither reads
# `.agents/skills/` directly, so this script materializes real copies there.
# GitHub Copilot and other AGENTS.md-aware tools read `.agents/skills/`
# natively and need no copy.
#
# Real copies (not symlinks) are used deliberately: Git symlinks are not
# reliably checked out on Windows without core.symlinks=true and OS-level
# symlink privilege, which silently degrades the link to a text stub.
#
# Run from anywhere; paths resolve relative to the repo root.
#   (no args)  regenerate the tool copies in place
#   --check    verify the committed copies match a fresh sync without
#              modifying the working tree (used by CI)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$REPO_ROOT/.agents/skills"
TARGETS=(".claude/skills" ".cursor/skills")

CHECK_ONLY=0
if [[ "${1:-}" == "--check" ]]; then
  CHECK_ONLY=1
fi

if [[ ! -d "$SRC" ]]; then
  echo "error: source skills directory not found: $SRC" >&2
  exit 1
fi

# Populate $1 with one copy of each canonical skill directory.
populate() {
  local dst="$1"
  rm -rf "$dst"
  mkdir -p "$dst"
  local skill name
  for skill in "$SRC"/*/; do
    name="$(basename "$skill")"
    cp -R "$skill" "$dst/$name"
  done
}

if [[ "$CHECK_ONLY" == "1" ]]; then
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  status=0
  for target in "${TARGETS[@]}"; do
    populate "$tmp/$target"
    if ! diff -r -q "$tmp/$target" "$REPO_ROOT/$target" >/dev/null 2>&1; then
      echo "error: $target is out of sync with .agents/skills/." >&2
      diff -r "$tmp/$target" "$REPO_ROOT/$target" >&2 || true
      status=1
    fi
  done
  if [[ "$status" != "0" ]]; then
    echo "Run scripts/sync-skills.sh and commit the result." >&2
    exit 1
  fi
  echo "Skills are in sync."
else
  for target in "${TARGETS[@]}"; do
    populate "$REPO_ROOT/$target"
  done
  echo "Synced .agents/skills/ -> ${TARGETS[*]}"
fi
