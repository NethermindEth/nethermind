#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${EEZ_UPSTREAM:?}"
: "${EEZ_CI_RESULT_DIR:?}"
: "${EEZ_RUNTIME_DIR:?}"
: "${EEZ_NETHERMIND_BASE_IMAGE:?}"
: "${EEZ_NETHERMIND_PROVER_BASE_IMAGE:?}"
: "${GITHUB_SHA:?}"
profile="$(jq -er '.profile' "$EEZ_CI_RESULT_DIR/upstream.json")"
[[ "$profile" == "${EEZ_PROFILE:?}" && ( "$profile" == rollup0 || "$profile" == rollupx ) ]] || { echo "unexpected live profile" >&2; exit 1; }
suffix="${GITHUB_RUN_ID:?}-${GITHUB_RUN_ATTEMPT:?}-$profile-${EEZ_SUITE:?}"
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
export EEZ_SKIP_NODE_BUILD=1
export EEZ_SKIP_DEPLOY_BUILD=1
export EEZ_PARTITION_SCRIPT="$HERE/partitions.py"
prepared_node="$(jq -er '.node | select(test("^sha256:[0-9a-f]{64}$"))' "$EEZ_CI_RESULT_DIR/prepared-images.json")"
prepared_deploy="$(jq -er '.deploy | select(test("^sha256:[0-9a-f]{64}$"))' "$EEZ_CI_RESULT_DIR/prepared-images.json")"
docker tag "$prepared_node" "$EEZ_NODE_IMAGE"
docker tag "$prepared_deploy" "$EEZ_DEPLOY_IMAGE"
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
            timeout 20s kurtosis service logs --all "$KURTOSIS_ENCLAVE" "$service" > "$EEZ_CI_RESULT_DIR/$service.log" 2>&1 || true
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
skip_consensus_calls=true
if [[ "$profile" == rollup0 ]]; then
    skip_consensus_calls="$(jq -er 'if .streaming.skip_consensus_layer_calls == true then "true" elif .streaming.skip_consensus_layer_calls == false then "false" else error("missing execution profile") end' "$EEZ_CI_RESULT_DIR/upstream.json")"
    docker build --build-arg SKIP_CONSENSUS_LAYER_CALLS="$skip_consensus_calls" -f "$HERE/Dockerfile.prover" --build-arg BASE_IMAGE="$EEZ_NETHERMIND_PROVER_BASE_IMAGE" -t "$EEZ_PROOF_SIGNER_IMAGE" "$HERE"
    prover_revision="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$EEZ_PROOF_SIGNER_IMAGE")"
    [[ "$prover_revision" == "$GITHUB_SHA" ]] || { echo "candidate prover image revision mismatch" >&2; exit 1; }
    prover_image_id="$(docker image inspect --format '{{.Id}}' "$EEZ_PROOF_SIGNER_IMAGE")"
    export EEZ_CONTROL_IMAGE_ID="$prover_image_id"
else
    docker tag "$EEZ_NODE_IMAGE" "$EEZ_PROOF_SIGNER_IMAGE"
    docker run --rm --entrypoint eez-proof-signer "$EEZ_PROOF_SIGNER_IMAGE" --help >/dev/null
    export EEZ_SKIP_NODE_BUILD=1
    export EEZ_CAUSAL_VERIFIER_BIN="$EEZ_RUNTIME_DIR/e2e-causal-verify"
    export EEZ_BLOB_VERIFIER_BIN="$EEZ_RUNTIME_DIR/verify_blob_evidence"
fi
docker build --build-arg PROFILE="$profile" --build-arg SKIP_CONSENSUS_LAYER_CALLS="$skip_consensus_calls" -f "$HERE/Dockerfile" --build-arg BASE_IMAGE="$EEZ_NETHERMIND_BASE_IMAGE" -t "$EEZ_NETHERMIND_IMAGE" "$HERE"
follower_image_id="$(docker image inspect --format '{{.Id}}' "$EEZ_NETHERMIND_IMAGE")"
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
run_timeout=13m
if [[ "$profile" == rollupx && "$EEZ_SUITE" == protocol ]]; then
    run_timeout=18m
fi
timeout --kill-after=5s "$run_timeout" bash "$EEZ_UPSTREAM/testing/kurtosis/run-ci.sh"
if [[ "$EEZ_SUITE" == recovery ]]; then
    test -s "$EEZ_CI_RESULT_DIR/fresh-follower.json"
    test -s "$EEZ_CI_RESULT_DIR/after-restart.json"
fi
[[ "$(docker image inspect --format '{{.Id}}' "$EEZ_NETHERMIND_IMAGE")" == "$follower_image_id" ]] \
    || { echo "candidate follower image was replaced during the upstream run" >&2; exit 1; }
jq -e '.result == "pass" and .proof_flow.signed_windows > 0 and .proof_flow.remote_attestations > 0' "$EEZ_CI_RESULT_DIR/result.json" >/dev/null
if [[ "$profile" == rollup0 ]]; then
    [[ "$(docker image inspect --format '{{.Id}}' "$EEZ_PROOF_SIGNER_IMAGE")" == "$prover_image_id" ]] \
        || { echo "candidate prover image was replaced during the upstream run" >&2; exit 1; }
    if [[ "$EEZ_SUITE" == recovery ]]; then test -s "$EEZ_CI_RESULT_DIR/streaming-controls.json"; fi
    python3 "$HERE/prepare.py" report --directory "$EEZ_CI_RESULT_DIR" --revision "$GITHUB_SHA" --image-id "$prover_image_id"
else
    python3 "$HERE/prepare.py" follower-report --directory "$EEZ_CI_RESULT_DIR" --revision "$GITHUB_SHA" --image-id "$follower_image_id"
fi
