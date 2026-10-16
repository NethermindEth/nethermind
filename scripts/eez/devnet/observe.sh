#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${KURTOSIS_ENCLAVE:?}"
: "${EEZ_CI_RESULT_DIR:?}"
: "${EEZ_UPSTREAM:?}"
phase="${1:?}"
# shellcheck source=/dev/null
source "$EEZ_UPSTREAM/testing/kurtosis/ports.sh" >/dev/null
deployment="$EEZ_CI_RESULT_DIR/deployment"
connection="$EEZ_CI_RESULT_DIR/connection"
if [[ ! -f "$deployment/deployments.env" ]]; then
    mkdir -p "$deployment" "$connection"
    kurtosis files download "$KURTOSIS_ENCLAVE" eez-deployments "$deployment" >/dev/null
    kurtosis files download "$KURTOSIS_ENCLAVE" nethermind-connection "$connection" >/dev/null
fi
set -a
# shellcheck source=/dev/null
source "$deployment/deployments.env"
set +a
endpoint() {
    local value
    value="$(kurtosis port print "$KURTOSIS_ENCLAVE" "$1" rpc)"
    if [[ "$value" != http://* && "$value" != https://* ]]; then value="http://$value"; fi
    printf '%s' "$value"
}
verify() {
    local service="$1" name="$2" minimum="$3" height="$4"
    local url ready=0
    url="$(endpoint "$service")"
    for ((attempt=0; attempt<60; attempt++)); do
        if cast block 0 --rpc-url "$url" >/dev/null 2>&1; then ready=1; break; fi
        sleep 2
    done
    (( ready == 1 )) || { echo "Nethermind genesis RPC did not become ready" >&2; return 1; }
    local genesis_args=()
    if [[ "$name" != verify-eezl2-deployment ]]; then
        genesis_args=(--genesis-evidence "$EEZ_CI_RESULT_DIR/verify-eezl2-deployment.json")
    fi
    python3 "$HERE/verify.py" \
        --l1 "$EEZ_DEVNET_L1_RPC" --reference "$EEZ_DEVNET_L2_RPC" --candidate "$url" \
        --registry "$EEZ_REGISTRY_ADDRESS" --registry-call "$(cast calldata 'rollups(uint64)' "$EEZ_ROLLUP_ID")" \
        --genesis "$EEZ_INITIAL_BLOCK_HASH" --l1-chain-id "$(cat "$connection/l1-chain-id")" \
        --l2-chain-id "$(cast chain-id --rpc-url "$EEZ_DEVNET_L2_RPC")" \
        --profile "${EEZ_PROFILE:-rollup0}" --phase "$name" --minimum-native "$minimum" --minimum-height "$height" \
        --output "$EEZ_CI_RESULT_DIR/$name.json" "${genesis_args[@]}"
}
if [[ "$phase" == recovery ]]; then
    verify nethermind-l2 before-restart 1 1
    height="$(jq -r '.height' "$EEZ_CI_RESULT_DIR/before-restart.json")"
    kurtosis service stop "$KURTOSIS_ENCLAVE" nethermind-l2
    kurtosis service start "$KURTOSIS_ENCLAVE" nethermind-l2
    verify nethermind-l2 after-restart 1 "$((height + 1))"
    height="$(jq -r '.height' "$EEZ_CI_RESULT_DIR/after-restart.json")"
    python3 "$HERE/prepare.py" late --output "$EEZ_CI_RESULT_DIR/late-package" \
        --connection "$connection" --follower-image "$EEZ_NETHERMIND_IMAGE"
    kurtosis run --enclave "$KURTOSIS_ENCLAVE" "$EEZ_CI_RESULT_DIR/late-package" \
        --args-file "$EEZ_CI_RESULT_DIR/late-package/args.json"
    verify nethermind-l2-late fresh-follower 1 "$height"
else
    minimum=1
    if [[ "$phase" == verify-eezl2-deployment ]]; then minimum=0; fi
    verify nethermind-l2 "$phase" "$minimum" 1
fi
