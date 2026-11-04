#!/usr/bin/env bash
# Generates a fresh Daisugi-style genesis: Prague/Electra from genesis, EIP-8141 from genesis, EIP-8288 and
# the Lighthouse Daisugi fork at DAISUGI_EPOCH. Amsterdam, Fulu and Gloas stay unscheduled.
# Usage: gen-genesis.sh <out-dir> [genesis-timestamp] [daisugi-epoch]
set -euo pipefail
umask 077

OUT=$(realpath -m "${1:?out dir}")
GENESIS_TIMESTAMP=${2:-$(( $(date +%s) + 20 ))}
DAISUGI_EPOCH=${3:-3}
GENERATOR=ethpandaops/ethereum-genesis-generator@sha256:287f596b9a4ee72ae2aff194682f854650a8861542e489259adf0975cc0c9a54
SECONDS_PER_SLOT=2
SLOTS_PER_EPOCH=32
VALIDATORS=${VALIDATORS:-64}
LOCAL=${LOCAL:-0}
PUBLIC_MNEMONICS=(
  "sleep moment list remain like wall lake industry canvas wonder ecology elite duck salad naive syrup frame brass utility club odor country obey pudding"
  "giant issue aisle success illegal bike spike question tent bar rely arctic volcano long crawl hungry vocal artwork sniff fantasy very lucky have athlete"
  "test test test test test test test test test test test junk"
  "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"
  "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art"
)
normalize() { tr -s ' \t\n' '   ' | tr '[:upper:]' '[:lower:]' | sed 's/^ //; s/ $//'; }
fail() { echo "$*" >&2; exit 1; }

[[ "$LOCAL" == 0 || "$LOCAL" == 1 ]] || fail "LOCAL must be 0 or 1"
[[ "$VALIDATORS" =~ ^[1-9][0-9]*$ ]] || fail "VALIDATORS must be a positive integer"
if [[ "$LOCAL" == 1 ]]; then
  CHAIN_ID=${CHAIN_ID:-1337}
  OPERATORS=${OPERATORS:-local:0:$VALIDATORS}
else
  [[ -n "${CHAIN_ID:-}" ]] || fail "CHAIN_ID is required for a shared network; LOCAL=1 keeps 1337"
  [[ -n "${OPERATORS:-}" ]] || fail "OPERATORS is required for a shared network: 'name:first:count ...' with disjoint validator ranges"
fi
[[ "$CHAIN_ID" =~ ^[1-9][0-9]{0,15}$ ]] || fail "CHAIN_ID must be a positive integer"
[[ "$LOCAL" == 1 || ( "$CHAIN_ID" != 1337 && "$CHAIN_ID" != 1 ) ]] || fail "CHAIN_ID $CHAIN_ID is public; pick a new one for a shared network"

declare -A OWNER=()
OPERATOR_LINES=()
for entry in $OPERATORS; do
  [[ "$entry" =~ ^([a-z0-9][a-z0-9-]*):(0|[1-9][0-9]*):([1-9][0-9]*)$ ]] || fail "malformed operator '$entry', want name:first:count"
  name=${BASH_REMATCH[1]} first=${BASH_REMATCH[2]} count=${BASH_REMATCH[3]}
  (( first + count <= VALIDATORS )) || fail "operator $name range $first..$(( first + count - 1 )) is past the $VALIDATORS validators"
  for line in "${OPERATOR_LINES[@]}"; do [[ "${line%% *}" == "$name" ]] && fail "operator $name is listed twice"; done
  for (( i = first; i < first + count; i++ )); do
    [[ -z "${OWNER[$i]:-}" ]] || fail "validator $i is assigned to both ${OWNER[$i]} and $name"
    OWNER[$i]=$name
  done
  OPERATOR_LINES+=("$name $first $count")
done
[[ ${#OPERATOR_LINES[@]} -gt 0 ]] || fail "OPERATORS lists no operator"
(( ${#OWNER[@]} == VALIDATORS )) || fail "OPERATORS covers ${#OWNER[@]} of the $VALIDATORS validators; assign every index"

if [[ -n "${DEVNET_MNEMONIC_FILE:-}" ]]; then
  [[ -f "$DEVNET_MNEMONIC_FILE" && ! -L "$DEVNET_MNEMONIC_FILE" ]] || fail "DEVNET_MNEMONIC_FILE is not a regular file"
  [[ $(stat -c %u "$DEVNET_MNEMONIC_FILE") == "$(id -u)" ]] || fail "DEVNET_MNEMONIC_FILE must be owned by $(id -un)"
  [[ $(stat -c %a "$DEVNET_MNEMONIC_FILE") =~ ^[46]00$ ]] || fail "DEVNET_MNEMONIC_FILE must have mode 0600 or 0400"
  MNEMONIC=$(normalize < "$DEVNET_MNEMONIC_FILE")
elif [[ -n "${DEVNET_MNEMONIC:-}" ]]; then
  MNEMONIC=$(normalize <<<"$DEVNET_MNEMONIC")
elif [[ "$LOCAL" == 1 ]]; then
  MNEMONIC=
else
  fail "DEVNET_MNEMONIC_FILE (mode 0600) or DEVNET_MNEMONIC is required for a shared network"
fi
if [[ "$LOCAL" == 0 ]]; then
  for public in "${PUBLIC_MNEMONICS[@]}"; do [[ "$MNEMONIC" == "$public" ]] && fail "refusing a public test mnemonic outside LOCAL=1"; done
fi

[[ -e "$OUT" ]] && { echo "$OUT exists; pick a new directory" >&2; exit 1; }
mkdir -p "$OUT/config"
SECRETS=$(mktemp -d)
trap 'status=$?; rm -rf "$SECRETS"; (( status == 0 )) || rm -rf "$OUT"' EXIT
# Disposable per-run mnemonic for the validators and premined accounts; DEVNET_MNEMONIC reuses one.
[[ -n "$MNEMONIC" ]] || MNEMONIC=$(docker run --rm --network none --entrypoint eth2-val-tools "$GENERATOR" mnemonic)
printf '%s\n' "$MNEMONIC" > "$SECRETS/mnemonic"

cat > "$SECRETS/values.env" <<EOF
export PRESET_BASE=mainnet
export CHAIN_ID=$CHAIN_ID
export EL_AND_CL_MNEMONIC="$MNEMONIC"
export NUMBER_OF_VALIDATORS=$VALIDATORS
export SLOT_DURATION_IN_SECONDS=$SECONDS_PER_SLOT
export SLOT_DURATION_MS=$(( SECONDS_PER_SLOT * 1000 ))
export GENESIS_TIMESTAMP=$GENESIS_TIMESTAMP
export GENESIS_DELAY=0
export GENESIS_GASLIMIT=60000000
export ELECTRA_FORK_EPOCH=0
export FULU_FORK_EPOCH=18446744073709551615
export GLOAS_FORK_EPOCH=18446744073709551615
export MAX_PAYLOAD_SIZE=20971520
EOF

generate() {
  docker run --rm --network none --user "$(id -u):$(id -g)" \
    -v "$SECRETS/values.env:/config/values.env:ro" -v "$OUT/data:/data" "$GENERATOR" "$@"
}

mkdir -p "$OUT/data"
generate el

# EIP-8141 from the genesis timestamp, EIP-8288 at the Daisugi fork boundary. Neither changes the genesis block:
# EIP-8288 adds header fields only from its activation.
EIP8288_TIME=$(( GENESIS_TIMESTAMP + DAISUGI_EPOCH * SLOTS_PER_EPOCH * SECONDS_PER_SLOT ))
GENESIS="$OUT/data/metadata/genesis.json"
jq --argjson t8141 "$GENESIS_TIMESTAMP" --argjson t8288 "$EIP8288_TIME" \
  '.config.eip8141PrototypeTime = $t8141 | .config.eip8288PrototypeTime = $t8288
   | del(.config.osakaTime, .config.amsterdamTime, .config.bogotaTime)' "$GENESIS" > "$GENESIS.tmp"
mv "$GENESIS.tmp" "$GENESIS"
[[ $(jq -r .config.chainId "$GENESIS") == "$CHAIN_ID" ]] || fail "genesis.json chainId is not $CHAIN_ID"

generate cl

CONFIG="$OUT/data/metadata/config.yaml"
cat >> "$CONFIG" <<EOF

# Daisugi: Electra plus the EIP-8288 proof in execution payloads, at the EIP-8288 boundary.
DAISUGI_FORK_VERSION: 0xd0000038
DAISUGI_FORK_EPOCH: $DAISUGI_EPOCH
EOF
echo 0 > "$OUT/data/metadata/deploy_block.txt"
JWT=$(openssl rand -hex 32)
mkdir -p "$OUT/data/jwt" && printf "0x%s" "$JWT" > "$OUT/data/jwt/jwtsecret"
echo "[]" > "$OUT/data/metadata/boot_enr.yaml"
rm -f "$OUT/data/metadata/mnemonics.yaml"
rc=0; grep -rqFf "$SECRETS/mnemonic" "$OUT/data" || rc=$?
(( rc == 1 )) || fail "the mnemonic leaked into $OUT/data, or the leak check failed (grep exit $rc)"

KEYSTORE_FLAGS=()
[[ "$LOCAL" == 1 ]] && KEYSTORE_FLAGS=(--insecure)
for line in "${OPERATOR_LINES[@]}"; do
  read -r name first count <<<"$line"
  if [[ "$LOCAL" == 1 && ${#OPERATOR_LINES[@]} == 1 ]]; then dir=validators; else dir=operators/$name; fi
  mkdir -p "$(dirname "$OUT/data/$dir")"
  docker run --rm --network none --user "$(id -u):$(id -g)" -v "$SECRETS/mnemonic:/secret/mnemonic:ro" -v "$OUT/data:/data" \
    --entrypoint sh "$GENERATOR" -c 'exec eth2-val-tools keystores "$@" --source-mnemonic "$(cat /secret/mnemonic)"' keystores \
    "${KEYSTORE_FLAGS[@]}" --source-min "$first" --source-max $(( first + count )) --out-loc "/data/$dir"
  [[ $(find "$OUT/data/$dir/keys" -mindepth 1 -maxdepth 1 -type d | wc -l) == "$count" ]] || fail "operator $name: expected $count keystores"
  echo "$name $first $count data/$dir" >> "$OUT/operators.txt"
done
if [[ "$LOCAL" == 1 ]]; then cp "$SECRETS/mnemonic" "$OUT/mnemonic"; cp "$SECRETS/values.env" "$OUT/config/values.env"; fi

jq -n --argjson genesis "$GENESIS_TIMESTAMP" --argjson eip8288 "$EIP8288_TIME" --argjson epoch "$DAISUGI_EPOCH" \
  --argjson chainId "$CHAIN_ID" --argjson validators "$VALIDATORS" --argjson local "$([[ $LOCAL == 1 ]] && echo true || echo false)" \
  --rawfile operators "$OUT/operators.txt" \
  --arg generator "$GENERATOR" --arg hash "$(jq -r '.latest_execution_payload_header.block_hash' "$OUT/data/parsed/parsedConsensusGenesis.json")" \
  '{generator: $generator, chainId: $chainId, secondsPerSlot: 2, slotsPerEpoch: 32, validators: $validators,
    genesisTimestamp: $genesis, eip8141PrototypeTime: $genesis, eip8288PrototypeTime: $eip8288,
    daisugiForkEpoch: $epoch, daisugiForkVersion: "0xd0000038", elGenesisHash: $hash, local: $local,
    operators: [$operators | split("\n")[] | select(. != "") | split(" ")
      | {name: .[0], first: (.[1] | tonumber), count: (.[2] | tonumber), dir: .[3]}]}' > "$OUT/params.json"
cat "$OUT/params.json"
