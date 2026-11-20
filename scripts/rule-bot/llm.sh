#!/usr/bin/env bash
set -euo pipefail
prompt_file=$1
tools=$2
shift 2
prompt=$(python3 -I - "$prompt_file" "$@" <<'EOF'
import os, re, sys
values = {}
for kv in sys.argv[2:]:
    key, value = kv.split("=", 1)
    values[key] = open(value).read() if os.path.isfile(value) else value
template = open(sys.argv[1]).read()
sys.stdout.write(re.sub(r"\{\{(\w+)\}\}", lambda m: values.get(m[1], m[0]), template))
EOF
)
claude -p "$prompt" --model "${RULE_BOT_MODEL:-opus}" --allowedTools "$tools" --max-turns "${RULE_BOT_MAX_TURNS:-80}"
