#!/bin/bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
config="$HERE/config.json"
native="$(/usr/bin/jq -er '.nativeSigner' "$config")"
profile="$(/usr/bin/jq -er '.profile' "$config")"

# The fixture starts registered members in order. Select one real candidate;
# every other member retains the upstream executable and arguments.
if ! mkdir "$HERE/selected" 2>/dev/null; then
    [[ -d "$HERE/selected" ]] || { echo "cannot select the candidate prover" >&2; exit 1; }
    if [[ "$profile" == rollupx ]]; then
        runtime="$(/usr/bin/jq -er '.runtime' "$config")"
        export LD_LIBRARY_PATH="$runtime/lib"
    fi
    exec "$native" "$@"
fi

listen=
genesis=
rollup=
vkey=
attester=
system=
l2_system=
manager=
registry=
l1_genesis=
while (( $# > 0 )); do
    [[ $# -ge 2 ]] || { echo "missing signer argument value" >&2; exit 1; }
    case "$1" in
        --listen-addr) listen="$2" ;;
        --chain-config) genesis="$2" ;;
        --rollup-id) rollup="$2" ;;
        --vkey) vkey="$2" ;;
        --attester-address) attester="$2" ;;
        --proof-system) system="$2" ;;
        --l2-system-address) l2_system="$2" ;;
        --manager-address) manager="$2" ;;
        --registry-address) registry="$2" ;;
        --l1-chain-config) l1_genesis="$2" ;;
        *) echo "unsupported signer argument: $1" >&2; exit 1 ;;
    esac
    shift 2
done
[[ "$listen" =~ ^127\.0\.0\.1:([0-9]{1,5})$ ]] || { echo "expected a loopback endpoint" >&2; exit 1; }
port="${BASH_REMATCH[1]}"
(( port > 0 && port <= 65535 )) || exit 1
[[ "$genesis" == /* && -f "$genesis" && "$genesis" != *,* ]] || exit 1
cp "$genesis" "$HERE/selected/genesis.json"
chmod 0644 "$HERE/selected/genesis.json"
genesis="$HERE/selected/genesis.json"
export DOCKER_CONFIG="$HERE/selected/docker-config"
mkdir "$DOCKER_CONFIG"
[[ "$rollup" =~ ^[1-9][0-9]*$ && "$vkey" =~ ^0x[0-9a-fA-F]{64}$ ]] || exit 1
for address in "$attester" "$system" "$l2_system"; do
    [[ "$address" =~ ^0x[0-9a-fA-F]{40}$ ]] || exit 1
done
: "${EEZ_PROOF_SIGNER_KEY:?}"
image="$(/usr/bin/jq -er '.image' "$config")"
name="$(/usr/bin/jq -er '.container' "$config")"
[[ "$image" =~ ^sha256:[0-9a-f]{64}$ && "$name" =~ ^eez-quorum-[a-z0-9-]+$ ]] || exit 1
mounts=()
settings=()
prover_args=()
cleanup=(--rm)
network=(--publish "127.0.0.1:$port:50061")
if [[ "$profile" == rollupx ]]; then
    [[ "$l1_genesis" == /* && -f "$l1_genesis" ]] || exit 1
    [[ "$manager" =~ ^0x[0-9a-fA-F]{40}$ && "$registry" =~ ^0x[0-9a-fA-F]{40}$ ]] || exit 1
    cp "$l1_genesis" "$HERE/selected/l1-genesis.json"
    chmod 0644 "$HERE/selected/l1-genesis.json"
    runtime="$(/usr/bin/jq -er '.runtime' "$config")"
    l2_hash="$(LD_LIBRARY_PATH="$runtime/lib" "$runtime/eez-genesis-block-hash" "$genesis")"
    [[ "$l2_hash" =~ ^0x[0-9a-fA-F]{64}$ ]] || exit 1
    block_time="$(/usr/bin/jq -er '.config.rollupX.blockIntervalSeconds | select(type == "number" and . > 0 and . == floor)' "$genesis")"
    mounts=(--mount "type=bind,src=$HERE/selected/l1-genesis.json,dst=/genesis/genesis.json,readonly")
    settings=(--env "EEZ_ROLLUP_MANAGER_ADDRESS=$manager" --env "EEZ_REGISTRY_ADDRESS=$registry"
              --env "EEZ_INITIAL_BLOCK_HASH=$l2_hash" --env "EEZ_L2_BLOCK_TIME_SECS=$block_time")
    prover_args=(--l1-chain-config=/genesis/genesis.json)
    # The runner removes this container after collecting its logs, even on startup failure.
    cleanup=()
    # The fixture detects readiness by connecting to this exact listener.
    network=(--network host --env "EEZ_CI_H2C_BIND=$listen")
elif [[ "$profile" != rollup0 || -n "$l1_genesis$manager$registry" ]]; then
    echo "unexpected quorum signer profile or arguments" >&2
    exit 1
fi
/usr/bin/jq -n --arg image "$image" --arg attester "$attester" --arg system "$system" \
    --arg rollup "$rollup" --arg endpoint "$listen" \
    '{implementation:"Nethermind", image:$image, attester:$attester, proofSystem:$system,
      rollupId:$rollup, endpoint:$endpoint}' > "$HERE/participant.json"
exec /usr/bin/docker run "${cleanup[@]}" --init --name "$name" \
    --label "nethermind.eez.quorum=$name" \
    "${network[@]}" \
    --mount "type=bind,src=$genesis,dst=/out/l2-genesis.json,readonly" \
    --memory 3g --cpus 1 --cap-drop ALL --security-opt no-new-privileges \
    --env EEZ_PROOF_SIGNER_KEY \
    --env "EEZ_ATTESTER_ADDRESS=$attester" --env "EEZ_PROOF_SYSTEM=$system" \
    --env "EEZ_VKEY=$vkey" --env "EEZ_ROLLUP_ID=$rollup" \
    --env "EEZ_L2_SYSTEM_ADDRESS=$l2_system" \
    "${mounts[@]}" "${settings[@]}" \
    "$image" --listen-addr=0.0.0.0:50061 --chain-config=/out/l2-genesis.json "${prover_args[@]}"
