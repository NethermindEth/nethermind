#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${EEZ_UPSTREAM:?}"
: "${EEZ_RUNTIME_DIR:?}"
: "${EEZ_CI_RESULT_DIR:?}"
cd "$EEZ_UPSTREAM"
forge build --root contracts --threads 1
forge build --root eez-core-protocol --threads 1
if [[ "$EEZ_PROFILE" == rollup0 ]]; then
    mkdir -p "$EEZ_CI_RESULT_DIR/embedded"
    python3 "$HERE/embedded.py" prepare --upstream "$EEZ_UPSTREAM" \
        --manifest "$EEZ_CI_RESULT_DIR/upstream.json" --output "$EEZ_CI_RESULT_DIR/embedded/inventory.json"
    python3 "$HERE/controls.py" install
    cargo test --locked --profile ci-e2e -p eez-node-e2e --no-run --message-format=json \
        --test durability --test resumption --test resumption_append --test native_system --test nethermind_streaming_controls \
        > "$EEZ_CI_RESULT_DIR/test-build.jsonl"
    python3 "$HERE/bundle.py" tests --directory "$EEZ_CI_RESULT_DIR"
fi
node_image="eez-upstream:$GITHUB_RUN_ID-$EEZ_PROFILE"
deploy_image="eez-deploy-base:$GITHUB_RUN_ID-$EEZ_PROFILE"
if [[ "$EEZ_PROFILE" == rollup0 ]]; then
    docker build -f testing/kurtosis/Dockerfile.prebuilt --target node -t "$node_image" "$EEZ_RUNTIME_DIR"
else
    docker build -f "$HERE/Dockerfile.upstream" -t "$node_image" "$EEZ_RUNTIME_DIR"
fi
docker build --build-arg "EEZ_NODE_IMAGE=$node_image" -f testing/kurtosis/Dockerfile.deploy -t "$deploy_image-base" .
docker build --build-arg "BASE_IMAGE=$deploy_image-base" -f "$HERE/Dockerfile.fixtures" -t "$deploy_image" .
jq -n --arg node "$(docker image inspect --format '{{.Id}}' "$node_image")" \
    --arg deploy "$(docker image inspect --format '{{.Id}}' "$deploy_image")" \
    '{node: $node, deploy: $deploy}' > "$EEZ_CI_RESULT_DIR/prepared-images.json"
docker save "$node_image" "$deploy_image" | zstd -T0 -3 > "$EEZ_CI_RESULT_DIR/upstream-images.tar.zst"
python3 "$HERE/bundle.py" pack --directory "$EEZ_CI_RESULT_DIR"
