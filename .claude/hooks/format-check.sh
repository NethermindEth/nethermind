#!/usr/bin/env bash
# PostToolUse hook: mirrors the two checks from .github/workflows/code-formatting.yml.
#
#   1. dotnet format whitespace  →  scripts/dotnet-format.sh  (auto-fix)
#   2. cspell                    →  scripts/cspell.sh          (report only)
#
# Both scripts live in the repo so any change to the CI logic is picked up here
# automatically. cspell.json is the shared config between the CI action and this hook.

input=$(cat)
if command -v jq &>/dev/null; then
    file=$(echo "$input" | jq -r '.tool_input.file_path // empty')
else
    # Fallback: use node (always available alongside npx/cspell)
    file=$(echo "$input" | node -e \
        "let d='';process.stdin.on('data',c=>d+=c);process.stdin.on('end',()=>process.stdout.write((JSON.parse(d).tool_input?.file_path??'')))")
fi

[[ "$file" == *.cs ]] || exit 0

# Normalise path separators (Windows backslashes → forward slashes)
file="${file//\\//}"

[[ "$file" == *"/src/Nethermind/"* ]] || exit 0

REPO_ROOT="${file%%/src/Nethermind/*}"
NM_ROOT="$REPO_ROOT/src/Nethermind"
rel="${file#$NM_ROOT/}"   # path relative to src/Nethermind/, as dotnet format expects

# 1. Auto-format whitespace (mirrors the Format step in the workflow)
bash "$REPO_ROOT/scripts/dotnet-format.sh" --include "$rel"

# 2. Spell-check the file (mirrors the Check spelling step in the workflow)
bash "$REPO_ROOT/scripts/cspell.sh" "$file"
