#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$here/lib.sh"

container="${1:?container}" rpc_url="${2:?RPC URL}" expected_head="${3:?snapshot head}"
required_from="${4:?first queried block}" required_to="${5:?last queried block}" out="${6:?output directory}"
for number in "$expected_head" "$required_from" "$required_to"; do
  [[ "$number" =~ ^[1-9][0-9]*$ ]] || die "invalid index coverage bound"
done
(( required_from <= required_to && required_to <= expected_head )) || die "invalid index coverage range"
mkdir -p "$out"

coverage_ready() {
  python3 - "$out/backfill.log" "$required_from" "$required_to" "$out/coverage.json" <<'PY'
import json
import re
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(errors="replace")
text = re.sub(r"\x1b\[[0-9;]*m", "", text)
if "Log index sync (Backward): completed." not in text:
    sys.exit(1)
for low, high in reversed(re.findall(r"Log index sync: ([0-9,]+) - ([0-9,]+) \(blocks:", text)):
    low, high = int(low.replace(",", "")), int(high.replace(",", ""))
    if low <= int(sys.argv[2]) and high >= int(sys.argv[3]):
        Path(sys.argv[4]).write_text(json.dumps({"index_from": low, "index_to": high,
            "required_from": int(sys.argv[2]), "required_to": int(sys.argv[3])}, indent=2))
        sys.exit(0)
sys.exit(1)
PY
}

deadline=$((SECONDS + 1800))
echo "Waiting for log index coverage ${required_from}..${required_to} in ${container}"
while true; do
  docker logs "$container" > "$out/backfill.log" 2>&1
  coverage_ready && break
  [[ "$(docker inspect "$container" --format '{{.State.Running}}')" == "true" ]] || die "index builder container stopped"
  (( SECONDS < deadline )) || die "log index did not complete with verified coverage within 1800s"
  sleep 10
done

# Graceful shutdown drains the compression queue before the measured warm-up.
docker stop --time 600 "$container" >/dev/null
[[ "$(docker inspect "$container" --format '{{.State.ExitCode}}')" == "0" ]] || die "index preparation did not stop cleanly"
docker logs "$container" > "$out/backfill.log" 2>&1
grep -q 'Log index storage stopped' "$out/backfill.log" || die "index storage did not finish stopping"
coverage_ready || die "index coverage was lost during preparation"
restart_time="$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
docker start "$container" >/dev/null
wait_for_rpc "$rpc_url" 180 "$container"
head="$(rpc_head "$rpc_url")"
[[ "$head" =~ ^0x[0-9a-fA-F]{1,15}$ ]] || die "invalid snapshot head after index preparation"
(( 16#${head#0x} == expected_head )) || die "snapshot head changed after index preparation"

deadline=$((SECONDS + 180))
while true; do
  docker logs --since "$restart_time" "$container" > "$out/restart.log" 2>&1
  if python3 - "$out/restart.log" "$required_to" <<'PY'
import re
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(errors="replace")
pivots = re.findall(r"Log index sync: using block ([0-9]+) as pivot\.", text)
sys.exit(0 if "Log index sync (Backward): completed." in text
    and pivots and int(pivots[-1]) >= int(sys.argv[2]) else 1)
PY
  then
    break
  fi
  (( SECONDS < deadline )) || die "persisted index coverage was not confirmed after restart"
  sleep 5
done
cat "$out/coverage.json"
