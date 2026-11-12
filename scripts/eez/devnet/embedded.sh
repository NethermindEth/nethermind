#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${EEZ_UPSTREAM:?}"
: "${EEZ_CI_RESULT_DIR:?}"
: "${EEZ_NETHERMIND_BASE_IMAGE:?}"
export EEZ_EMBEDDED_RESULTS="$EEZ_CI_RESULT_DIR/embedded"
export EEZ_EMBEDDED_SCRIPT="$HERE/embedded.py"
export EEZ_CANDIDATE_RUN="eez-embedded-${GITHUB_RUN_ID:?}-${GITHUB_RUN_ATTEMPT:?}"
export EEZ_CANDIDATE_IMAGE
EEZ_CANDIDATE_IMAGE="$(docker image inspect --format '{{.Id}}' "$EEZ_NETHERMIND_BASE_IMAGE")"
export EEZ_CANDIDATE_USER
EEZ_CANDIDATE_USER="$(id -u):$(id -g)"
export EEZ_CANDIDATE_SKIP_CONSENSUS_CALLS
if [[ "$EEZ_PROFILE" == rollupx ]]; then
    EEZ_CANDIDATE_SKIP_CONSENSUS_CALLS=false
    export EEZ_TEST_PREBUILT=true
    export EEZ_TEST_RUNTIME_BIN_DIR="$EEZ_RUNTIME_DIR"
    export EEZ_TEST_PROOF_SIGNER_BIN="$EEZ_RUNTIME_DIR/eez-proof-signer"
    export CARGO_MANIFEST_DIR="$EEZ_UPSTREAM/crates/eez-testkit"
else
    EEZ_CANDIDATE_SKIP_CONSENSUS_CALLS="$(jq -er '.streaming.skip_consensus_layer_calls | if type == "boolean" then tostring else error("missing execution profile") end' "$EEZ_CI_RESULT_DIR/upstream.json")"
fi
mkdir -p "$EEZ_EMBEDDED_RESULTS"
cleanup() {
    local status=$?
    trap - EXIT
    while read -r container; do
        [[ -n "$container" ]] || continue
        docker logs "$container" > "$EEZ_EMBEDDED_RESULTS/$container.log" 2>&1 || true
        docker rm -f "$container" >/dev/null || true
    done < <(docker ps -aq --filter "label=nethermind.eez.run=$EEZ_CANDIDATE_RUN")
    exit "$status"
}
trap cleanup EXIT
test -s "$EEZ_EMBEDDED_RESULTS/inventory.json"
export EEZ_TEST_LOG_DIR="$EEZ_EMBEDDED_RESULTS/logs"
mkdir -p "$EEZ_TEST_LOG_DIR"
(
    cd "$EEZ_UPSTREAM"
    suite="${EEZ_SUITE#embedded-}"
    if [[ "$EEZ_PROFILE" == rollupx ]]; then
        binary="$(jq -er '.node_e2e' "$EEZ_CI_RESULT_DIR/test-binaries.json")"
        timeout 10m "$EEZ_UPSTREAM/$binary" "$suite::" --test-threads=1
    else
        binary="$(jq -er --arg suite "$suite" '.[$suite]' "$EEZ_CI_RESULT_DIR/test-binaries.json")"
        timeout 10m "$EEZ_UPSTREAM/$binary" --test-threads=1
    fi
)
python3 "$HERE/embedded.py" report --inventory "$EEZ_EMBEDDED_RESULTS/inventory.json" --directory "$EEZ_EMBEDDED_RESULTS"
