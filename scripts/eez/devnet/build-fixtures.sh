#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${EEZ_UPSTREAM:?}"
: "${EEZ_RUNTIME_DIR:?}"
: "${EEZ_CI_RESULT_DIR:?}"
cd "$EEZ_UPSTREAM"
forge build --root contracts --threads 1
forge build --root eez-core-protocol --threads 1
node_image="eez-upstream:$GITHUB_RUN_ID-$EEZ_PROFILE"
deploy_image="eez-deploy-base:$GITHUB_RUN_ID-$EEZ_PROFILE"
if [[ "$EEZ_PROFILE" == rollup0 ]]; then
    docker build -f testing/kurtosis/Dockerfile.prebuilt --target node -t "$node_image" "$EEZ_RUNTIME_DIR"
else
    docker build -f "$HERE/Dockerfile.upstream" -t "$node_image" "$EEZ_RUNTIME_DIR"
fi
docker build --build-arg "EEZ_NODE_IMAGE=$node_image" -f testing/kurtosis/Dockerfile.deploy -t "$deploy_image-base" .
(
    fixture_context="$(mktemp -d "${RUNNER_TEMP:?}/eez-fixtures.XXXXXXXX")"
    trap 'rm -rf "$fixture_context"' EXIT
    mkdir -p "$fixture_context/contracts" "$fixture_context/eez-core-protocol"
    cp -a contracts/out contracts/cache "$fixture_context/contracts/"
    cp -a eez-core-protocol/out eez-core-protocol/cache "$fixture_context/eez-core-protocol/"
    cp -a "$HOME/.svm" "$fixture_context/solc"
    docker build --build-arg "BASE_IMAGE=$deploy_image-base" -f "$HERE/Dockerfile.fixtures" -t "$deploy_image" "$fixture_context"
)
jq -n --arg node "$(docker image inspect --format '{{.Id}}' "$node_image")" \
    --arg deploy "$(docker image inspect --format '{{.Id}}' "$deploy_image")" \
    '{node: $node, deploy: $deploy}' > "$EEZ_CI_RESULT_DIR/prepared-images.json"
docker save "$node_image" "$deploy_image" | zstd -T0 -3 > "$EEZ_CI_RESULT_DIR/upstream-images.tar.zst"
python3 "$HERE/bundle.py" pack --directory "$EEZ_CI_RESULT_DIR"
