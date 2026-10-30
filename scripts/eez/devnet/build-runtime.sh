#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ "$EEZ_PROFILE" == rollup0 ]]; then
  cd "$EEZ_UPSTREAM"
  mkdir -p "$EEZ_CI_RESULT_DIR/embedded"
  python3 "$HERE/embedded.py" prepare --upstream "$EEZ_UPSTREAM" \
    --manifest "$EEZ_CI_RESULT_DIR/upstream.json" --output "$EEZ_CI_RESULT_DIR/embedded/inventory.json"
  python3 "$HERE/controls.py" install
  runtime_targets=()
  if test ! -x "$EEZ_RUNTIME_DIR/eez-composer"; then
    runtime_targets=(-p eez-node -p eez-follower -p eez-prover-stateless --bins --examples)
  fi
  cargo build --locked --profile ci-e2e -p eez-node-e2e "${runtime_targets[@]}" --message-format=json --timings \
    --test durability --test resumption --test resumption_append --test native_system --test contract_post --test partial_consumption --test nethermind_streaming_controls \
    > "$EEZ_CI_RESULT_DIR/test-build.jsonl"
  cp target/cargo-timings/cargo-timing.html "$EEZ_CI_RESULT_DIR/cargo-timing.html"
  python3 "$HERE/bundle.py" tests --directory "$EEZ_CI_RESULT_DIR"
fi
if [[ "$EEZ_PROFILE" == rollupx ]]; then
  cd "$EEZ_UPSTREAM"
  mkdir -p "$EEZ_CI_RESULT_DIR/embedded"
  python3 "$HERE/embedded.py" prepare --upstream "$EEZ_UPSTREAM" \
    --manifest "$EEZ_CI_RESULT_DIR/upstream.json" --output "$EEZ_CI_RESULT_DIR/embedded/inventory.json"
  CARGO_PROFILE_DEV_DEBUG=0 CARGO_PROFILE_DEV_SPLIT_DEBUGINFO=off \
    cargo build --locked -p eez-node-e2e --test node_e2e --message-format=json --timings \
    > "$EEZ_CI_RESULT_DIR/test-build.jsonl"
  cp target/cargo-timings/cargo-timing.html "$EEZ_CI_RESULT_DIR/cargo-timing.html"
  python3 "$HERE/bundle.py" tests --directory "$EEZ_CI_RESULT_DIR"
fi
if test ! -x "$EEZ_RUNTIME_DIR/eez-composer"; then
  if [[ "$EEZ_PROFILE" == rollup0 ]]; then
    cp target/ci-e2e/eez-composer target/ci-e2e/eez-follower target/ci-e2e/eez-proof-signer "$EEZ_RUNTIME_DIR/"
    cp target/ci-e2e/examples/genesis_state_root "$EEZ_RUNTIME_DIR/eez-genesis-state-root"
    cp target/ci-e2e/examples/genesis_block_hash "$EEZ_RUNTIME_DIR/eez-genesis-block-hash"
  else
    rust_image="$(sed -n 's/^ARG RUST_IMAGE=//p' "$EEZ_UPSTREAM/Dockerfile.runtime")"
    [[ "$rust_image" =~ ^rust:[0-9]+\.[0-9]+\.[0-9]+-bookworm$ ]] || { echo "upstream build/runtime platform changed" >&2; exit 1; }
    docker run --rm -v "$EEZ_UPSTREAM:/build" -v "$EEZ_RUNTIME_DIR:/out" -w /build "$rust_image" bash -euo pipefail -c '
      apt-get update
      apt-get install -y clang libclang-dev pkg-config libssl-dev cmake protobuf-compiler mold
      bash scripts/ci/install-llvm.sh
      export LLVM_SYS_221_PREFIX=/usr/lib/llvm-22 PATH="/usr/lib/llvm-22/bin:$PATH" RUST_MIN_STACK=16777216
      git config --global --add safe.directory /build
      git config --file .gitmodules --get-regexp "^submodule\\..*\\.path$" | while read -r key path; do
        git config --global --add safe.directory "/build/$path"
      done
      export RUSTFLAGS="-C link-arg=-fuse-ld=mold"
      cargo build --locked --release -p eez-node -p eez-follower -p eez-proof-signer -p eez-protocol -p eez-l1 \
        --bin eez-composer --bin eez-follower --bin eez-proof-signer \
        --example genesis_state_root --example genesis_block_hash --example e2e-causal-verify --example verify_blob_evidence
      cp target/release/eez-composer target/release/eez-follower target/release/eez-proof-signer /out/
      cp target/release/examples/e2e-causal-verify target/release/examples/verify_blob_evidence /out/
      cp target/release/examples/genesis_state_root /out/eez-genesis-state-root
      cp target/release/examples/genesis_block_hash /out/eez-genesis-block-hash
      rustc --version > /out/rustc-version.txt
      cargo --version > /out/cargo-version.txt
    '
  fi
  python3 "$GITHUB_WORKSPACE/scripts/eez/devnet/upstream.py" record-build --manifest "$EEZ_CI_RESULT_DIR/upstream.json" --checkout "$EEZ_UPSTREAM" --output "$EEZ_RUNTIME_DIR"
fi
echo "$EEZ_RUNTIME_DIR" >> "$GITHUB_PATH"
