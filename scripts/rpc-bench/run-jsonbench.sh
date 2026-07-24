#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run json-bench (NethermindEth/json-bench) against already-running JSON-RPC
# node(s). Two modes:
#   * benchmark — k6-driven load benchmark of the primary node (and, when a
#     reference node is up, of both side by side). Reports come from k6's own
#     summary.json: the pinned json-bench builds per-client/per-method metrics
#     from it directly, so no Prometheus is involved (--prometheus is omitted).
#     A benchmark_config points the load at a curated workload (e.g. the repo's
#     head-only configs); its client list is rewritten to the node(s) here.
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
# Underscore, not dash: the registry validator rejects dashes in client names.
[[ -n "$REFERENCE_RPC_URL" && "$REFERENCE_LABEL" == "$LABEL" ]] && REFERENCE_LABEL="${REFERENCE_LABEL}_ref"

JB_REPO="${JB_REPO:-https://github.com/NethermindEth/json-bench.git}"
# Pin to a specific commit so a push to the repo's default branch cannot
# silently change benchmark results (or run unreviewed code on the runner).
# Override JB_REF (sha/tag/branch) or JB_REPO to move it. This commit makes
# --prometheus optional (metrics built from summary.json) and drops the
# invalid eth_getStorageAt corpus entry.
JB_REF="${JB_REF:-89c65c73f4325e8b6e1de2c520690bf468eb6c52}"
JB_MODE="${JB_MODE:-}"                       # benchmark | compare; empty = auto
# Benchmark workload: a bare name resolves to config/benchmark/<name>.yaml, a
# value with '/' is a repo-relative path, and an absolute path is read as-is.
# Empty = a generated default read mix. Any config's client list is rewritten to
# the node(s) started here, so the repo's multi-client head configs work as-is.
JB_BENCHMARK_CONFIG="${JB_BENCHMARK_CONFIG:-}"
JB_COMPARE_CONFIG="${JB_COMPARE_CONFIG:-config/compare/defaults.yaml}"
JB_RPS="${JB_RPS:-}"                         # override the workload's rps; empty = keep it (generated default: 100)
JB_DURATION="${JB_DURATION:-}"               # override the workload's k6 duration; empty = keep it (generated default: 60s)
JB_VUS="${JB_VUS:-}"                         # override the workload's vus; empty = keep it (generated default: 10)
JB_CONCURRENCY="${JB_CONCURRENCY:-5}"        # compare mode
JB_TIMEOUT="${JB_TIMEOUT:-30}"               # compare mode, per-request seconds
JB_VALIDATE_SCHEMA="${JB_VALIDATE_SCHEMA:-false}"
JB_HTML_REPORT="${JB_HTML_REPORT:-true}"
# Response differences are reported (and warned about) by default; opt in to
# failing the step on any diff once the method set is curated for the clients.
JB_FAIL_ON_DIFF="${JB_FAIL_ON_DIFF:-false}"
# Benchmark gate: k6 exits 0 even at a 100% HTTP failure rate (the injected
# per-call thresholds are loose by design), so the step fails itself when the
# summary.json http fail rate exceeds this percentage.
JB_MAX_FAIL_RATE_PCT="${JB_MAX_FAIL_RATE_PCT:-1}"
JB_EXTRA_ARGS="${JB_EXTRA_ARGS:-}"
CONTAINER_NAME="${JB_CONTAINER_NAME:-jsonbench-bench}"

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

# Resolve a compare-config path. The compare loader runs it through
# SafeReadPath, which rejects absolute paths and '..' — so the value passed to
# --config must stay RELATIVE to the container's /jb working directory (the
# checkout). Repo-relative paths pass through unchanged; an absolute host path
# is copied into the checkout host-side (the /jb mount is ro only in the
# container) and passed by its relative name.
resolve_config() {
  local cfg="$1"
  if [[ "$cfg" == /* ]]; then
    [[ -f "$cfg" ]] || die "config '$cfg' not found"
    cp "$cfg" "$work/src/rpc-bench-custom.yaml"
    echo "rpc-bench-custom.yaml"
  else
    [[ -f "$work/src/$cfg" ]] || die "config '$cfg' not found in the json-bench checkout"
    echo "$cfg"
  fi
}

if [[ "$JB_MODE" == "benchmark" && -z "$JB_BENCHMARK_CONFIG" ]]; then
  # Default workload: the same mainnet read mix as the repo's mixed.yaml, but
  # targeting OUR registry names (the repo config references its own examples).
  # The per-call thresholds are loose (never trip) and only exist to make k6
  # emit a per-method http_req_duration sub-metric into summary.json.
  {
    echo "test_name: \"RPC read benchmark ($LABEL${REFERENCE_RPC_URL:+ vs $REFERENCE_LABEL})\""
    echo "description: \"Snapshot-backed read-path benchmark on the reproducible-benchmarks runner\""
    echo "clients:"
    echo "  - $LABEL"
    [[ -n "$REFERENCE_RPC_URL" ]] && echo "  - $REFERENCE_LABEL"
    echo "duration: \"${JB_DURATION:-60s}\""
    echo "rps: ${JB_RPS:-100}"
    echo "vus: ${JB_VUS:-10}"
    cat <<'CALLS'
calls:
  - name: "WETH balance eth_call"
    method: "eth_call"
    params:
      - to: "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2"
        data: "0x70a08231000000000000000000000000000000000000000000000000000000000000000a"
    weight: 40
    thresholds: ["p(99)<600000"]
  - name: "eth_getBalance"
    method: "eth_getBalance"
    params:
      - "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045"
      - "latest"
    weight: 20
    thresholds: ["p(99)<600000"]
  - name: "eth_blockNumber"
    method: "eth_blockNumber"
    params: []
    weight: 20
    thresholds: ["p(99)<600000"]
  - name: "eth_getTransactionCount"
    method: "eth_getTransactionCount"
    params:
      - "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045"
      - "latest"
    weight: 10
    thresholds: ["p(99)<600000"]
  - name: "eth_getBlockByNumber"
    method: "eth_getBlockByNumber"
    params:
      - "latest"
      - false
    weight: 10
    thresholds: ["p(99)<600000"]
CALLS
  } > "$work/io/benchmark.yaml"
  bench_cfg="/io/benchmark.yaml"
elif [[ "$JB_MODE" == "benchmark" ]]; then
  # Adapt a curated config to this run. The repo's head-only configs list five
  # clients — which won't match a single-node run here — so we rewrite the client
  # list to the node(s) started here, inject a loose per-call threshold (per-method
  # sub-metrics), and apply any rps/vus/duration override. Their ./rpc-calls/*.jsonl
  # fixtures stay relative and resolve via the container's /jb working directory.
  # A bare name maps to config/benchmark/<name>.yaml.
  case "$JB_BENCHMARK_CONFIG" in
    /*)  src_bench="$JB_BENCHMARK_CONFIG" ;;
    */*) src_bench="$work/src/$JB_BENCHMARK_CONFIG" ;;
    *)   src_bench="$work/src/config/benchmark/${JB_BENCHMARK_CONFIG}.yaml" ;;
  esac
  [[ -f "$src_bench" ]] || die "benchmark_config '$JB_BENCHMARK_CONFIG' not found (looked at $src_bench)"

  python3 -c 'import yaml' 2>/dev/null \
    || python3 -m pip install --user pyyaml 2>/dev/null \
    || python3 -m pip install --user --break-system-packages pyyaml \
    || die "PyYAML is required to adapt the benchmark config and could not be installed"

  ref_label=""
  [[ -n "$REFERENCE_RPC_URL" ]] && ref_label="$REFERENCE_LABEL"
  JB_PRIMARY_LABEL="$LABEL" JB_REF_LABEL="$ref_label" \
  JB_RPS="$JB_RPS" JB_VUS="$JB_VUS" JB_DURATION="$JB_DURATION" \
  python3 - "$src_bench" "$work/io/benchmark.yaml" <<'PY'
import os, sys, yaml

src, out = sys.argv[1], sys.argv[2]
with open(src) as f:
    cfg = yaml.safe_load(f) or {}

clients = [os.environ["JB_PRIMARY_LABEL"]]
if os.environ.get("JB_REF_LABEL"):
    clients.append(os.environ["JB_REF_LABEL"])
cfg["clients"] = clients

for key in ("rps", "vus"):
    v = os.environ.get("JB_" + key.upper(), "").strip()
    if v:
        cfg[key] = int(v)
dur = os.environ.get("JB_DURATION", "").strip()
if dur:
    cfg["duration"] = dur

# Fixture paths stay relative to the checkout root: the container runs with its
# working directory at /jb (the mounted checkout) and json-bench's loader rejects
# absolute paths (SafeReadPath), so the config's ./rpc-calls/... resolve as-is.
for call in cfg.get("calls", []) or []:
    if not call.get("thresholds"):
        call["thresholds"] = ["p(99)<600000"]

with open(out, "w") as f:
    yaml.safe_dump(cfg, f, sort_keys=False, default_flow_style=False)
PY
  log "Adapted benchmark_config '$JB_BENCHMARK_CONFIG' -> clients=[$LABEL${ref_label:+, $ref_label}]"
  bench_cfg="/io/benchmark.yaml"
fi

# The runner image executes as a non-root user (uid 1001) — open up the io
# mount so it can write outputs there (scratch-only, wiped next run).
chmod -R a+rwX "$work/io"

# Word-split JB_EXTRA_ARGS without glob expansion.
read -ra extra_args_arr <<< "$JB_EXTRA_ARGS"

docker_common=(
  --rm --name "$CONTAINER_NAME"
  --network host
  # CWD at the checkout root so a config's relative ./rpc-calls/*.jsonl fixtures
  # resolve (json-bench's loader forbids absolute paths).
  -w /jb
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
  # No Prometheus: the pinned json-bench builds per-client/per-method metrics
  # straight from k6's summary.json when --prometheus is omitted. k6 still writes
  # summary.json (via --summary-export) regardless, and the per-call thresholds
  # above give it the per-method sub-metrics.
  html=()
  [[ "$JB_HTML_REPORT" == "true" ]] && html=(--html-report)
  log "json-bench benchmark (config: ${JB_BENCHMARK_CONFIG:-<generated default>}, summary.json metrics)..."
  docker run "${docker_common[@]}" "$image_tag" \
    benchmark \
    --config "$bench_cfg" \
    --clients /io/clients.yaml \
    --output /io/out \
    ${html[@]+"${html[@]}"} \
    ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/jsonbench.log" \
    || tool_failed=1
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
  # Effective workload params come from the rendered config (generated default or
  # adapted curated config), read back with grep so no YAML lib is needed here.
  bench_meta="$work/io/benchmark.yaml"
  disp_dur="$(sed -nE 's/^duration:[[:space:]]*"?([^"#]*)"?[[:space:]]*$/\1/p' "$bench_meta" 2>/dev/null | head -1)"
  disp_rps="$(sed -nE 's/^rps:[[:space:]]*([0-9]+).*/\1/p' "$bench_meta" 2>/dev/null | head -1)"
  disp_vus="$(sed -nE 's/^vus:[[:space:]]*([0-9]+).*/\1/p' "$bench_meta" 2>/dev/null | head -1)"

  # Parse k6's summary.json into overall + per-method tables (stdlib json only).
  # The parser also emits the http fail rate (percent) for the gate below, and
  # a parse failure is remembered — a present-but-unparseable summary.json must
  # fail the step, not silently degrade to a "NO RESULTS" summary that passes.
  perf_md="$OUT_DIR/.jsonbench-perf.md"
  fail_pct_file="$OUT_DIR/.jsonbench-failrate"
  : > "$perf_md"
  summary_parse_failed=0
  fail_pct=""
  if [[ -s "$OUT_DIR/summary.json" ]]; then
    python3 - "$OUT_DIR/summary.json" "$perf_md" "$fail_pct_file" <<'PY' || summary_parse_failed=1
import json, re, sys
with open(sys.argv[1]) as f:
    metrics = (json.load(f) or {}).get("metrics", {}) or {}
def num(m, k):
    if not isinstance(m, dict):
        return 0.0
    v = m.get(k)
    if isinstance(v, (int, float)):
        return float(v)
    v = (m.get("values") or {}).get(k)
    return float(v) if isinstance(v, (int, float)) else 0.0
def r2(x):
    return round(x, 2)
rn = re.compile(r"req_name:([^,}]+)")
d = metrics.get("http_req_duration", {})
r = metrics.get("http_reqs", {})
fail = metrics.get("http_req_failed", {})
chk = metrics.get("checks", {})
fail_rate = num(fail, "rate") or num(fail, "value")
cp, cf = num(chk, "passes"), num(chk, "fails")
out = []
out.append("### Overall")
out.append("")
out.append("| metric | value |")
out.append("|---|---:|")
out.append("| requests | %d |" % int(num(r, "count")))
out.append("| throughput (req/s) | %s |" % r2(num(r, "rate")))
out.append("| http fail rate | %s%% |" % r2(fail_rate * 100))
if (cp + cf) > 0:
    out.append("| checks passed | %s%% |" % r2(cp / (cp + cf) * 100))
for label, key in [("avg", "avg"), ("p50", "med"), ("p90", "p(90)"),
                   ("p95", "p(95)"), ("p99", "p(99)"), ("max", "max")]:
    out.append("| latency %s (ms) | %s |" % (label, r2(num(d, key))))
rows = []
for key, val in metrics.items():
    if key.startswith("http_req_duration{") and "req_name:" in key:
        m = rn.search(key)
        if m:
            rows.append((m.group(1).strip().strip("'").strip('"'), val))
if rows:
    out.append("")
    out.append("### Per method (http_req_duration, ms)")
    out.append("")
    out.append("| method | avg | p50 | p90 | p95 | p99 | max |")
    out.append("|---|---:|---:|---:|---:|---:|---:|")
    for name, val in sorted(rows, key=lambda x: x[0]):
        out.append("| %s | %s | %s | %s | %s | %s | %s |" % (
            name, r2(num(val, "avg")), r2(num(val, "med")), r2(num(val, "p(90)")),
            r2(num(val, "p(95)")), r2(num(val, "p(99)")), r2(num(val, "max"))))
with open(sys.argv[2], "w") as f:
    f.write("\n".join(out) + "\n")
with open(sys.argv[3], "w") as f:
    f.write("%.4f\n" % (fail_rate * 100))
PY
    fail_pct="$(head -n 1 "$fail_pct_file" 2>/dev/null || true)"
    rm -f "$fail_pct_file"
  fi

  {
    echo "## RPC Benchmark — json-bench (k6)"
    echo
    echo "Node(s): \`$LABEL\` = \`$RPC_URL\`${REFERENCE_RPC_URL:+, \`$REFERENCE_LABEL\` = \`$REFERENCE_RPC_URL\`} | config: \`${JB_BENCHMARK_CONFIG:-<generated default>}\` | duration: \`${disp_dur:-?}\` | rps: \`${disp_rps:-?}\` | vus: \`${disp_vus:-?}\`"
    echo
    if [[ -s "$perf_md" ]]; then
      cat "$perf_md"
      echo
    fi
    if [[ -s "$OUT_DIR/results.csv" ]]; then
      echo "<details><summary>results.csv (first 60 lines)</summary>"
      echo
      echo '```csv'
      head -n 60 "$OUT_DIR/results.csv"
      echo '```'
      echo
      echo "</details>"
      echo
    fi
    html_note=""
    [[ "$JB_HTML_REPORT" == "true" && -s "$OUT_DIR/report.html" ]] && html_note=" / \`report.html\`"
    if [[ -s "$perf_md" || -s "$OUT_DIR/results.csv" ]]; then
      echo "Full results: \`summary.json\` / \`results.json\` / \`results.csv\`${html_note} in the artifact."
    else
      echo "**NO RESULTS** — json-bench wrote neither \`summary.json\` nor \`results.csv\` (see \`jsonbench.log\` in the artifact)."
    fi
    echo
  } > "$summary"
  rm -f "$perf_md"
fi
log "json-bench summary written to $summary"

if [[ "$tool_failed" == "1" ]]; then
  die "json-bench exited non-zero — failing the benchmark step (see jsonbench.log)"
fi
if [[ "$JB_MODE" == "benchmark" && ! -s "$OUT_DIR/summary.json" && ! -s "$OUT_DIR/results.csv" ]]; then
  die "json-bench benchmark produced no summary.json or results.csv — failing the benchmark step"
fi
if [[ "$JB_MODE" == "benchmark" && -s "$OUT_DIR/summary.json" && "${summary_parse_failed:-0}" == "1" ]]; then
  die "summary.json exists but could not be parsed — failing the benchmark step (file is in the artifact)"
fi
if [[ "$JB_MODE" == "benchmark" && -n "${fail_pct:-}" ]] \
    && awk -v f="${fail_pct:-0}" -v m="$JB_MAX_FAIL_RATE_PCT" 'BEGIN { exit !(f > m) }'; then
  die "http fail rate ${fail_pct}% exceeds max_fail_rate_pct=${JB_MAX_FAIL_RATE_PCT}% — failing the benchmark step"
fi
if [[ "$JB_MODE" == "compare" && -z "$diff_count" ]]; then
  die "json-bench compare produced no parseable results — failing the benchmark step"
fi
if [[ "$JB_FAIL_ON_DIFF" == "true" && -n "$diff_count" && "$diff_count" != "0" ]]; then
  die "json-bench compare found $diff_count response difference(s) and fail_on_diff is enabled"
fi
