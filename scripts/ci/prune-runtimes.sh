#!/usr/bin/env bash
# Drop foreign <os>-<arch> native RID folders from a build output tree so the per-RID
# artifact is lean. Bare-OS parents (linux, unix, win, osx) are kept — they hold managed
# fallback assemblies the target RID resolves via the RID-fallback graph.
set -euo pipefail

rid="${1:?usage: prune-runtimes.sh <target-rid> [bin-root]}"
bin_root="${2:-src/Nethermind/artifacts/bin}"

[ -d "$bin_root" ] || { echo "::error::bin root not found: $bin_root"; exit 1; }

before=$(du -sm "$bin_root" 2>/dev/null | cut -f1 || echo '?')

# Collect to a temp file (no `mapfile` — macOS ships bash 3.2), then delete, so find
# isn't walking a directory being removed underneath it.
list=$(mktemp)
find "$bin_root" -type d -path '*/runtimes/*' \
  | awk -F/ -v rid="$rid" '$(NF-1) == "runtimes" && $NF ~ /-/ && $NF != rid' \
  | sort -u > "$list"

removed=0
while IFS= read -r d; do
  [ -n "$d" ] || continue
  rm -rf "$d" && removed=$((removed + 1))
done < "$list"
rm -f "$list"

after=$(du -sm "$bin_root" 2>/dev/null | cut -f1 || echo '?')

# Target RID should survive (unless there are no native deps) — else the rule/RID is wrong.
if ! find "$bin_root" -type d -path "*/runtimes/$rid" -print -quit | grep -q .; then
  echo "::warning::no runtimes/$rid folder present after prune for rid=$rid"
fi

echo "prune-runtimes[$rid]: ${before}MB -> ${after}MB (removed ${removed} foreign RID dirs)"
