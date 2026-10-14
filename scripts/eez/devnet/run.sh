#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${EEZ_UPSTREAM:?}"
: "${EEZ_CI_RESULT_DIR:?}"
: "${EEZ_RUNTIME_DIR:?}"
: "${EEZ_NETHERMIND_BASE_IMAGE:?}"
: "${EEZ_NETHERMIND_PROVER_BASE_IMAGE:?}"
: "${GITHUB_SHA:?}"
suffix="${GITHUB_RUN_ID:?}-${GITHUB_RUN_ATTEMPT:?}"
export KURTOSIS_ENCLAVE="eez-nm-$suffix"
export EEZ_NODE_IMAGE="eez-composer:$suffix"
export EEZ_PROOF_SIGNER_IMAGE="eez-proof-signer:$suffix"
export EEZ_DEPLOY_IMAGE="eez-deploy:$suffix"
export EEZ_NETHERMIND_IMAGE="eez-nethermind:$suffix"
export NETHERMIND_OBSERVE_SCRIPT="$HERE/observe.sh"
export EEZ_PREBUILT_BIN_DIR="$EEZ_RUNTIME_DIR"
export KURTOSIS_PRIVILEGED=0
export EEZ_PRUNE_BUILD_CACHE=0
export EEZ_SKIP_PROOF_SIGNER_BUILD=1
mkdir -p "$EEZ_CI_RESULT_DIR"
{
    free -h
    df -h "$EEZ_CI_RESULT_DIR"
    docker system df
} > "$EEZ_CI_RESULT_DIR/resources.txt"
cleanup() {
    local status=$?
    trap - EXIT
    for service in nethermind-l2 nethermind-l2-late eez-proof-signer eez-node; do
        if [[ ! -s "$EEZ_CI_RESULT_DIR/$service.log" ]]; then
            timeout 20s kurtosis service logs "$KURTOSIS_ENCLAVE" "$service" > "$EEZ_CI_RESULT_DIR/$service.log" 2>&1 || true
        fi
    done
    docker image inspect "$EEZ_NODE_IMAGE" "$EEZ_PROOF_SIGNER_IMAGE" "$EEZ_DEPLOY_IMAGE" \
        "$EEZ_NETHERMIND_IMAGE" "$EEZ_NETHERMIND_BASE_IMAGE" "$EEZ_NETHERMIND_PROVER_BASE_IMAGE" > "$EEZ_CI_RESULT_DIR/images.json" 2>/dev/null || true
    kurtosis enclave rm -f "$KURTOSIS_ENCLAVE" || true
    docker image rm "$EEZ_NODE_IMAGE" "$EEZ_PROOF_SIGNER_IMAGE" "$EEZ_DEPLOY_IMAGE" \
        "$EEZ_NETHERMIND_IMAGE" "$EEZ_NETHERMIND_BASE_IMAGE" "$EEZ_NETHERMIND_PROVER_BASE_IMAGE" || true
    exit "$status"
}
trap cleanup EXIT
docker build -f "$HERE/Dockerfile" --build-arg BASE_IMAGE="$EEZ_NETHERMIND_BASE_IMAGE" -t "$EEZ_NETHERMIND_IMAGE" "$HERE"
docker build -f "$HERE/Dockerfile.prover" --build-arg BASE_IMAGE="$EEZ_NETHERMIND_PROVER_BASE_IMAGE" -t "$EEZ_PROOF_SIGNER_IMAGE" "$HERE"
prover_revision="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$EEZ_PROOF_SIGNER_IMAGE")"
[[ "$prover_revision" == "$GITHUB_SHA" ]] || { echo "candidate prover image revision mismatch" >&2; exit 1; }
prover_image_id="$(docker image inspect --format '{{.Id}}' "$EEZ_PROOF_SIGNER_IMAGE")"
python3 "$HERE/prepare.py" prepare --manifest "$EEZ_CI_RESULT_DIR/upstream.json" \
    --upstream "$EEZ_UPSTREAM" --output "$EEZ_CI_RESULT_DIR/ci-args-template.yaml" \
    --node-image "$EEZ_NODE_IMAGE" --signer-image "$EEZ_PROOF_SIGNER_IMAGE" \
    --deploy-image "$EEZ_DEPLOY_IMAGE" --follower-image "$EEZ_NETHERMIND_IMAGE"
export KURTOSIS_ARGS_FILE="$EEZ_CI_RESULT_DIR/ci-args-template.yaml"
{
    kurtosis version
    forge --version
    docker version
    git -C "$EEZ_UPSTREAM" rev-parse HEAD
    git -C "$EEZ_UPSTREAM/eez-core-protocol" rev-parse HEAD
} > "$EEZ_CI_RESULT_DIR/tools.txt"
timeout 80m bash "$EEZ_UPSTREAM/testing/kurtosis/run-ci.sh"
test -s "$EEZ_CI_RESULT_DIR/fresh-follower.json"
test -s "$EEZ_CI_RESULT_DIR/after-restart.json"
[[ "$(docker image inspect --format '{{.Id}}' "$EEZ_PROOF_SIGNER_IMAGE")" == "$prover_image_id" ]] \
    || { echo "candidate prover image was replaced during the upstream run" >&2; exit 1; }
jq -e '.result == "pass" and .proof_flow.signed_windows > 0 and .proof_flow.remote_attestations > 0' "$EEZ_CI_RESULT_DIR/result.json" >/dev/null
jq --arg revision "$GITHUB_SHA" --arg image_id "$prover_image_id" \
    '{result: .result, implementation: "Nethermind", wire: "prove.v1", profile: "legacy-rollup0-calldata", candidate_revision: $revision, image_id: $image_id, proof_flow: .proof_flow}' \
    "$EEZ_CI_RESULT_DIR/result.json" > "$EEZ_CI_RESULT_DIR/candidate-prover.json"
