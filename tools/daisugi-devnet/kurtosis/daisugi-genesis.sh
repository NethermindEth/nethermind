#!/usr/bin/env bash
set -euo pipefail
/work/upstream-entrypoint.sh "$@"
[[ "${1:-}" == all ]] || exit 0
fail() { echo "daisugi-genesis: $*" >&2; exit 1; }
# ethereum-package passes ethereum_genesis_generator_params.extra_env, DAISUGI_FORK_EPOCH among them, through values.env.
source /config/values.env
GENESIS=/data/metadata/genesis.json
CONFIG=/data/metadata/config.yaml
# The fork times come from the generated CL config, the one the beacon nodes load: a missing value fails here
# instead of shifting the fork.
cfg() {
  local v
  v=$(sed -n "s/^$1: *\([0-9][0-9]*\).*/\1/p" "$CONFIG")
  [[ -n $v ]] || fail "$1 missing from config.yaml"
  echo "$v"
}
[[ ${DAISUGI_FORK_EPOCH:-} =~ ^[0-9]+$ ]] || fail "DAISUGI_FORK_EPOCH must be set to an epoch number"
grep -q '^PRESET_BASE: *mainnet' "$CONFIG" || fail "the fork time assumes the mainnet preset (32 slots per epoch)"
SLOT_MS=$(cfg SLOT_DURATION_MS)
MIN_GENESIS_TIME=$(cfg MIN_GENESIS_TIME)
GENESIS_DELAY=$(cfg GENESIS_DELAY)
(( SLOT_MS % 1000 == 0 )) || fail "SLOT_DURATION_MS $SLOT_MS is not whole seconds"
CL_GENESIS_TIME=$(( MIN_GENESIS_TIME + GENESIS_DELAY ))
T8141=$(( $(jq -r .timestamp "$GENESIS") ))
T8288=$(( CL_GENESIS_TIME + DAISUGI_FORK_EPOCH * 32 * SLOT_MS / 1000 ))
jq --argjson t8141 "$T8141" --argjson t8288 "$T8288" \
  '.config.eip8141PrototypeTime = $t8141 | .config.eip8288PrototypeTime = $t8288
   | del(.config.osakaTime, .config.amsterdamTime, .config.bogotaTime)' "$GENESIS" > "$GENESIS.tmp"
mv "$GENESIS.tmp" "$GENESIS"
sed -i '/^SECONDS_PER_SLOT:/d' "$CONFIG"
printf '\nDAISUGI_FORK_VERSION: 0xd0000038\nDAISUGI_FORK_EPOCH: %s\n' "$DAISUGI_FORK_EPOCH" >> "$CONFIG"
