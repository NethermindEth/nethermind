#!/usr/bin/env bash
set -euo pipefail
prompt_file=$1
tools=$2
shift 2
here=$(cd "$(dirname "$0")" && pwd)
prompt=$("$here/rule-bot" render "$prompt_file" "$@")
deny=()
[ -z "${RULE_BOT_DENY:-}" ] || deny=(--disallowedTools "$RULE_BOT_DENY")
claude -p "$prompt" --model "${RULE_BOT_MODEL:-opus}" --allowedTools "$tools" "${deny[@]}" --max-turns "${RULE_BOT_MAX_TURNS:-80}"
