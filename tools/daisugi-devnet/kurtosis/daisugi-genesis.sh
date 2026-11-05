#!/usr/bin/env bash
set -euo pipefail
/work/upstream-entrypoint.sh "$@"
[[ "${1:-}" == all ]] || exit 0
source /config/values.env
GENESIS=/data/metadata/genesis.json
T8141=$(( $(jq -r .timestamp "$GENESIS") ))
T8288=$(( T8141 + DAISUGI_FORK_EPOCH * 32 * SLOT_DURATION_MS / 1000 ))
jq --argjson t8141 "$T8141" --argjson t8288 "$T8288" \
  '.config.eip8141PrototypeTime = $t8141 | .config.eip8288PrototypeTime = $t8288
   | del(.config.osakaTime, .config.amsterdamTime, .config.bogotaTime)' "$GENESIS" > "$GENESIS.tmp"
mv "$GENESIS.tmp" "$GENESIS"
sed -i '/^SECONDS_PER_SLOT:/d' /data/metadata/config.yaml
printf '\nDAISUGI_FORK_VERSION: 0xd0000038\nDAISUGI_FORK_EPOCH: %s\n' "$DAISUGI_FORK_EPOCH" >> /data/metadata/config.yaml
