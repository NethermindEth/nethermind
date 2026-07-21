#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run json-bench (NethermindEth/json-bench) against already-running JSON-RPC
# node(s). Two modes:
#   * benchmark — k6-driven load benchmark of the primary node (and, when a
#     reference node is up, of both side by side). json-bench streams k6 metrics
#     to Prometheus remote-write, so a throwaway local Prometheus container is
#     started for the duration of the run.
#   * compare   — one-shot differential test: the same requests are sent to the
#     primary and reference nodes and their responses are diffed
#     (comparison-results.json + comparison-report.html).
# The mode defaults to 'compare' when REFERENCE_RPC_URL is set, 'benchmark'
# otherwise. The runner is built from a pinned commit via its own Dockerfile
# (final image bundles the k6 binary) and runs on the host network.
#
# Tool-specific knobs are read from env (the workflow fills them from the
# `tool_config` JSON): JB_REF, JB_MODE, JB_BENCHMARK_CONFIG, JB_COMPARE_CONFIG,
# JB_RPS, JB_DURATION, JB_VUS, JB_CONCURRENCY, JB_TIMEOUT, JB_VALIDATE_SCHEMA,
# JB_HTML_REPORT, JB_FAIL_ON_DIFF, JB_EXTRA_ARGS.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

RPC_URL="${RPC_URL:-http://localhost:8545}"
: "${OUT_DIR:?output directory for json-bench results}"
: "${SCRATCH_ROOT:?writable scratch root}"
REFERENCE_RPC_URL="${REFERENCE_RPC_URL:-}"
LABEL="${LABEL:-${CLIENT_TYPE:-nethermind}}"
CLIENT_TYPE="${CLIENT_TYPE:-$LABEL}"
REFERENCE_LABEL="${REFERENCE_LABEL:-${REFERENCE_CLIENT_TYPE:-reference}}"
REFERENCE_CLIENT_TYPE="${REFERENCE_CLIENT_TYPE:-$REFERENCE_LABEL}"
# json-bench addresses nodes by registry name — keep them distinct (NM-vs-NM).
[[ -n "$REFERENCE_RPC_URL" && "$REFERENCE_LABEL" == "$LABEL" ]] && REFERENCE_LABEL="${REFERENCE_LABEL}-ref"

JB_REPO="${JB_REPO:-https://github.com/NethermindEth/json-bench.git}"
# Pin to a specific commit so a push to the repo's default branch cannot
# silently change benchmark results (or run unreviewed code on the runner).
# Override JB_REF (sha/tag/branch) or JB_REPO to move it.
JB_REF="${JB_REF:-fb5e558a14700b24aae3f3d35fb245f2d90ccf5a}"
JB_MODE="${JB_MODE:-}"                       # benchmark | compare; empty = auto
JB_BENCHMARK_CONFIG="${JB_BENCHMARK_CONFIG:-}"  # repo-relative or absolute path; empty = generated default
JB_COMPARE_CONFIG="${JB_COMPARE_CONFIG:-config/compare/defaults.yaml}"
JB_RPS="${JB_RPS:-100}"                      # generated benchmark workload only
JB_DURATION="${JB_DURATION:-60s}"            # generated benchmark workload only (k6 duration string)
JB_VUS="${JB_VUS:-10}"                       # generated benchmark workload only
JB_CONCURRENCY="${JB_CONCURRENCY:-5}"        # compare mode
JB_TIMEOUT="${JB_TIMEOUT:-30}"               # compare mode, per-request seconds
JB_VALIDATE_SCHEMA="${JB_VALIDATE_SCHEMA:-false}"
JB_HTML_REPORT="${JB_HTML_REPORT:-true}"
# Response differences are reported (and warned about) by default; opt in to
# failing the step on any diff once the method set is curated for the clients.
JB_FAIL_ON_DIFF="${JB_FAIL_ON_DIFF:-false}"
JB_EXTRA_ARGS="${JB_EXTRA_ARGS:-}"
JB_PROM_IMAGE="${JB_PROM_IMAGE:-prom/prometheus:v3.5.0}"
CONTAINER_NAME="${JB_CONTAINER_NAME:-jsonbench-bench}"
PROM_CONTAINER_NAME="${CONTAINER_NAME}-prom"

if [[ -z "$JB_MODE" ]]; then
  if [[ -n "$REFERENCE_RPC_URL" ]]; then JB_MODE="compare"; else JB_MODE="benchmark"; fi
fi
case "$JB_MODE" in
  benchmark) ;;
  compare)
    [[ -n "$REFERENCE_RPC_URL" ]] || die "JB_MODE=compare needs a reference node (set reference_client)"
    ;;
  *) die "unknown JB_MODE '$JB_MODE' (expected benchmark | compare)" ;;
esac

mkdir -p "$OUT_DIR"
SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")"
assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
work="$SCRATCH_ROOT/jsonbench"
# The runner container may have left non-owner files in scratch on a prior run.
as_root rm -rf "$work"
mkdir -p "$work/io/out"

cleanup_containers() {
  docker rm -f "$PROM_CONTAINER_NAME" >/dev/null 2>&1 || true
}
trap cleanup_containers EXIT

# ---------------------------------------------------------------------------
# Fetch the tool source and build the runner image (bundles k6).
# ---------------------------------------------------------------------------
log "Cloning $JB_REPO@$JB_REF..."
# Shallow-fetch a single ref; accepts a commit sha, tag, or branch (GitHub
# serves reachable commit shas), unlike 'git clone --branch'.
git init -q "$work/src"
git -C "$work/src" remote add origin "$JB_REPO"
git -C "$work/src" fetch -q --depth 1 origin "$JB_REF" \
  || die "failed to fetch $JB_REF from $JB_REPO"
git -C "$work/src" checkout -q FETCH_HEAD

runner_dockerfile="$work/src/runner/Dockerfile"
[[ -f "$runner_dockerfile" ]] || die "json-bench runner Dockerfile not found at $runner_dockerfile"
# Branch refs may contain '/' etc. — sanitize into a valid docker tag.
tag_ref="${JB_REF//[^a-zA-Z0-9_.-]/-}"
image_tag="jsonbench-runner:${tag_ref:0:24}"
log "Building $image_tag from runner/Dockerfile..."
docker build -q -f "$runner_dockerfile" -t "$image_tag" "$work/src" >/dev/null \
  || die "failed to build the json-bench runner image"

# ---------------------------------------------------------------------------
# Render the client registry (and, for benchmark mode, the default workload).
# ---------------------------------------------------------------------------
clients_yaml="$work/io/clients.yaml"
{
  echo "clients:"
  echo "  - name: \"$LABEL\""
  echo "    type: \"$CLIENT_TYPE\""
  echo "    url: \"$RPC_URL\""
  echo "    timeout: \"60s\""
  echo "    max_retries: 3"
  if [[ -n "$REFERENCE_RPC_URL" ]]; then
    echo "  - name: \"$REFERENCE_LABEL\""
    echo "    type: \"$REFERENCE_CLIENT_TYPE\""
    echo "    url: \"$REFERENCE_RPC_URL\""
    echo "    timeout: \"60s\""
    echo "    max_retries: 3"
  fi
} > "$clients_yaml"
log "Client registry:"
sed 's/^/  /' "$clients_yaml"

# Resolve a config path into an in-container path. Absolute host paths are
# copied into the io mount; repo-relative paths resolve against the checkout.
resolve_config() {
  local cfg="$1"
  if [[ "$cfg" == /* ]]; then
    [[ -f "$cfg" ]] || die "config '$cfg' not found"
    cp "$cfg" "$work/io/$(basename "$cfg")"
    echo "/io/$(basename "$cfg")"
  else
    [[ -f "$work/src/$cfg" ]] || die "config '$cfg' not found in the json-bench checkout"
    echo "/jb/$cfg"
  fi
}

if [[ "$JB_MODE" == "benchmark" && -z "$JB_BENCHMARK_CONFIG" ]]; then
  # Default workload: the same mainnet read mix as the repo's mixed.yaml, but
  # targeting OUR registry names (the repo config references its own examples).
  {
    echo "test_name: \"RPC read benchmark ($LABEL${REFERENCE_RPC_URL:+ vs $REFERENCE_LABEL})\""
    echo "description: \"Snapshot-backed read-path benchmark on the reproducible-benchmarks runner\""
    echo "clients:"
    echo "  - $LABEL"
    [[ -n "$REFERENCE_RPC_URL" ]] && echo "  - $REFERENCE_LABEL"
    echo "duration: \"$JB_DURATION\""
    echo "rps: $JB_RPS"
    echo "vus: $JB_VUS"
    cat <<'CALLS'
calls:
  - name: "WETH balance eth_call"
    method: "eth_call"
    params:
      - to: "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2"
        data: "0x70a08231000000000000000000000000000000000000000000000000000000000000000a"
    weight: 40
  - name: "eth_getBalance"
    method: "eth_getBalance"
    params:
      - "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045"
      - "latest"
    weight: 20
  - name: "eth_blockNumber"
    method: "eth_blockNumber"
    params: []
    weight: 20
  - name: "eth_getTransactionCount"
    method: "eth_getTransactionCount"
    params:
      - "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045"
      - "latest"
    weight: 10
  - name: "eth_getBlockByNumber"
    method: "eth_getBlockByNumber"
    params:
      - "latest"
      - false
    weight: 10
CALLS
  } > "$work/io/benchmark.yaml"
  bench_cfg="/io/benchmark.yaml"
elif [[ "$JB_MODE" == "benchmark" ]]; then
  # NB: a custom workload's `clients:` list must reference the registry names
  # rendered above ($LABEL / $REFERENCE_LABEL).
  bench_cfg="$(resolve_config "$JB_BENCHMARK_CONFIG")"
fi

# The runner image executes as a non-root user (uid 1001) — open up the io
# mount so it can write outputs there (scratch-only, wiped next run).
chmod -R a+rwX "$work/io"

# Word-split JB_EXTRA_ARGS without glob expansion.
read -ra extra_args_arr <<< "$JB_EXTRA_ARGS"

docker_common=(
  --rm --name "$CONTAINER_NAME"
  --network host
  -v "$work/src:/jb:ro"
  -v "$work/io:/io"
)
# A stale same-name container from a hard-interrupted run would fail docker run.
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# Run the selected mode.
# ---------------------------------------------------------------------------
tool_failed=0
if [[ "$JB_MODE" == "compare" ]]; then
  # Diffing nodes at different heads is meaningless ('latest' diverges).
  assert_same_head "$RPC_URL" "$REFERENCE_RPC_URL"

  compare_cfg="$(resolve_config "$JB_COMPARE_CONFIG")"
  validate=()
  [[ "$JB_VALIDATE_SCHEMA" == "true" ]] && validate=(--validate-schema)
  log "json-bench compare: $LABEL vs $REFERENCE_LABEL (config: $JB_COMPARE_CONFIG)..."
  docker run "${docker_common[@]}" "$image_tag" \
    compare \
    --config "$compare_cfg" \
    --clients /io/clients.yaml \
    --client-refs "$LABEL,$REFERENCE_LABEL" \
    --concurrency "$JB_CONCURRENCY" \
    --timeout "$JB_TIMEOUT" \
    --output /io/out \
    ${validate[@]+"${validate[@]}"} \
    ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/jsonbench.log" \
    || tool_failed=1
else
  # json-bench streams k6 metrics via Prometheus remote-write — provide a
  # throwaway local Prometheus (data lives in the container, discarded on rm).
  log "Starting throwaway Prometheus ($JB_PROM_IMAGE) for k6 remote-write..."
  docker rm -f "$PROM_CONTAINER_NAME" >/dev/null 2>&1 || true
  docker run -d --name "$PROM_CONTAINER_NAME" --network host "$JB_PROM_IMAGE" \
    --config.file=/etc/prometheus/prometheus.yml \
    --storage.tsdb.path=/prometheus \
    --web.enable-remote-write-receiver \
    || die "failed to start Prometheus container"
  elapsed=0
  until curl -sf "http://localhost:9090/-/ready" >/dev/null 2>&1; do
    sleep 2
    elapsed=$((elapsed + 2))
    if (( elapsed >= 120 )); then
      docker logs "$PROM_CONTAINER_NAME" 2>&1 | tail -n 40 || true
      die "Prometheus never became ready within 120s"
    fi
  done

  html=()
  [[ "$JB_HTML_REPORT" == "true" ]] && html=(--html-report)
  log "json-bench benchmark (config: ${JB_BENCHMARK_CONFIG:-<generated default>}, duration $JB_DURATION @ $JB_RPS rps)..."
  docker run "${docker_common[@]}" "$image_tag" \
    benchmark \
    --config "$bench_cfg" \
    --clients /io/clients.yaml \
    --prometheus "http://localhost:9090" \
    --output /io/out \
    ${html[@]+"${html[@]}"} \
    ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/jsonbench.log" \
    || tool_failed=1

  docker rm -f "$PROM_CONTAINER_NAME" >/dev/null 2>&1 || true
fi

# ---------------------------------------------------------------------------
# Collect outputs and build a markdown summary.
# ---------------------------------------------------------------------------
as_root chown -R "$(id -u):$(id -g)" "$work/io" 2>/dev/null || true
if [[ -d "$work/io/out" ]]; then
  cp -r "$work/io/out/." "$OUT_DIR/" 2>/dev/null || true
fi
cp "$clients_yaml" "$OUT_DIR/clients.yaml" 2>/dev/null || true

summary="$OUT_DIR/jsonbench-summary.md"
diff_count=""
if [[ "$JB_MODE" == "compare" ]]; then
  results="$OUT_DIR/comparison-results.json"
  {
    echo "## RPC Comparison — json-bench compare"
    echo
    echo "\`$LABEL\` = \`$RPC_URL\` vs \`$REFERENCE_LABEL\` = \`$REFERENCE_RPC_URL\` | config: \`$JB_COMPARE_CONFIG\`"
    echo
    if [[ -s "$results" ]]; then
      # Results are an array of {method, params, responses, differences, ...};
      # normalize defensively in case a future version wraps them in an object.
      total="$(jq -r 'if type == "array" then . else (.results // .comparisons // []) end | length' "$results" 2>/dev/null || echo "?")"
      diff_count="$(jq -r '[ (if type == "array" then . else (.results // .comparisons // []) end)[]
                            | select((.differences // {}) | length > 0) ] | length' "$results" 2>/dev/null || echo "")"
      echo "**${total} calls compared, ${diff_count:-?} with response differences.**"
      echo
      if [[ -n "$diff_count" && "$diff_count" != "0" ]]; then
        echo "| method | params | differences |"
        echo "|---|---|---|"
        jq -r '(if type == "array" then . else (.results // .comparisons // []) end)[]
               | select((.differences // {}) | length > 0)
               | "| \(.method) | \((.params // []) | tojson | .[0:80]) | \((.differences | keys) | join(", ")) |"' \
          "$results" 2>/dev/null | head -n 50 || true
        echo
        echo "Full diff detail: \`comparison-results.json\` / \`comparison-report.html\` in the artifact."
      fi
    else
      echo "**NO RESULTS** — json-bench did not write \`comparison-results.json\` (see \`jsonbench.log\` in the artifact)."
    fi
    echo
  } > "$summary"
else
  {
    echo "## RPC Benchmark — json-bench (k6)"
    echo
    echo "Node(s): \`$LABEL\` = \`$RPC_URL\`${REFERENCE_RPC_URL:+, \`$REFERENCE_LABEL\` = \`$REFERENCE_RPC_URL\`} | duration: \`$JB_DURATION\` | rps: \`$JB_RPS\` | vus: \`$JB_VUS\`"
    echo
    if [[ -s "$OUT_DIR/results.csv" ]]; then
      echo "<details><summary>results.csv (first 60 lines)</summary>"
      echo
      echo '```csv'
      head -n 60 "$OUT_DIR/results.csv"
      echo '```'
      echo
      echo "</details>"
      echo
      html_note=""
      [[ "$JB_HTML_REPORT" == "true" ]] && html_note=" / \`report.html\`"
      echo "Full results: \`results.json\` / \`results.csv\`${html_note} in the artifact."
    else
      echo "**NO RESULTS** — json-bench did not write \`results.csv\` (see \`jsonbench.log\` in the artifact)."
    fi
    echo
  } > "$summary"
fi
log "json-bench summary written to $summary"

if [[ "$tool_failed" == "1" ]]; then
  die "json-bench exited non-zero — failing the benchmark step (see jsonbench.log)"
fi
if [[ "$JB_MODE" == "compare" && -z "$diff_count" ]]; then
  die "json-bench compare produced no parseable results — failing the benchmark step"
fi
if [[ "$JB_FAIL_ON_DIFF" == "true" && -n "$diff_count" && "$diff_count" != "0" ]]; then
  die "json-bench compare found $diff_count response difference(s) and fail_on_diff is enabled"
fi
