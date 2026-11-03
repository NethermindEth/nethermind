#!/usr/bin/env bash
# Generates a fresh Daisugi-style genesis: Prague/Electra from genesis, EIP-8141 from genesis, EIP-8288 and
# the Lighthouse Daisugi fork at DAISUGI_EPOCH. Amsterdam, Fulu and Gloas stay unscheduled.
# Usage: gen-genesis.sh <out-dir> [genesis-timestamp] [daisugi-epoch]
set -euo pipefail

OUT=$(realpath -m "${1:?out dir}")
GENESIS_TIMESTAMP=${2:-$(( $(date +%s) + 20 ))}
DAISUGI_EPOCH=${3:-3}
GENERATOR=ethpandaops/ethereum-genesis-generator@sha256:287f596b9a4ee72ae2aff194682f854650a8861542e489259adf0975cc0c9a54
SECONDS_PER_SLOT=2
SLOTS_PER_EPOCH=32
VALIDATORS=64

[[ -e "$OUT" ]] && { echo "$OUT exists; pick a new directory" >&2; exit 1; }
mkdir -p "$OUT/config"
# Disposable per-run mnemonic for the validators and premined accounts; DEVNET_MNEMONIC reuses one.
MNEMONIC=${DEVNET_MNEMONIC:-$(docker run --rm --network none --entrypoint eth2-val-tools "$GENERATOR" mnemonic)}
printf '%s\n' "$MNEMONIC" > "$OUT/mnemonic" && chmod 600 "$OUT/mnemonic"

cat > "$OUT/config/values.env" <<EOF
export PRESET_BASE=mainnet
export CHAIN_ID=1337
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
    -v "$OUT/config/values.env:/config/values.env:ro" -v "$OUT/data:/data" "$GENERATOR" "$@"
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

generate cl

CONFIG="$OUT/data/metadata/config.yaml"
cat >> "$CONFIG" <<EOF

# Daisugi: Electra plus the EIP-8288 proof in execution payloads, at the EIP-8288 boundary.
DAISUGI_FORK_VERSION: 0xd0000038
DAISUGI_FORK_EPOCH: $DAISUGI_EPOCH
EOF
echo 0 > "$OUT/data/metadata/deploy_block.txt"
mkdir -p "$OUT/data/jwt" && printf "0x%s" "$(openssl rand -hex 32)" > "$OUT/data/jwt/jwtsecret"
echo "[]" > "$OUT/data/metadata/boot_enr.yaml"

docker run --rm --network none --user "$(id -u):$(id -g)" -v "$OUT/data:/data" --entrypoint eth2-val-tools "$GENERATOR" \
  keystores --insecure --source-mnemonic "$MNEMONIC" --source-min 0 --source-max "$VALIDATORS" --out-loc /data/validators

jq -n --argjson genesis "$GENESIS_TIMESTAMP" --argjson eip8288 "$EIP8288_TIME" --argjson epoch "$DAISUGI_EPOCH" \
  --arg generator "$GENERATOR" --arg hash "$(jq -r '.latest_execution_payload_header.block_hash' "$OUT/data/parsed/parsedConsensusGenesis.json")" \
  '{generator: $generator, chainId: 1337, secondsPerSlot: 2, slotsPerEpoch: 32, validators: 64,
    genesisTimestamp: $genesis, eip8141PrototypeTime: $genesis, eip8288PrototypeTime: $eip8288,
    daisugiForkEpoch: $epoch, daisugiForkVersion: "0xd0000038", elGenesisHash: $hash}' > "$OUT/params.json"
cat "$OUT/params.json"
