#!/usr/bin/env bash
set -euo pipefail
prompt_file=$1
tools=$2
shift 2
prompt=$(cat "$prompt_file")
for kv in "$@"; do
  key=${kv%%=*}
  value=${kv#*=}
  [ -f "$value" ] && value=$(cat "$value")
  prompt=${prompt//"{{$key}}"/$value}
done
claude -p "$prompt" --model "${RULE_BOT_MODEL:-opus}" --allowedTools "$tools" --max-turns "${RULE_BOT_MAX_TURNS:-80}"
