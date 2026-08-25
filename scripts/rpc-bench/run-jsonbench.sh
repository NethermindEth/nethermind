#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run json-bench (NethermindEth/json-bench) against running node(s): 'benchmark' (k6 load) or 'compare' (response diff).

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
# json-bench registry names must be distinct and may not contain dashes.
[[ -n "$REFERENCE_RPC_URL" && "$REFERENCE_LABEL" == "$LABEL" ]] && REFERENCE_LABEL="${REFERENCE_LABEL}_ref"

JB_REPO="${JB_REPO:-https://github.com/NethermindEth/json-bench.git}"
JB_REF="${JB_REF:-de1bcfadea47258ccacae2f420141032a82a9ded}"
JB_MODE="${JB_MODE:-}"
JB_BENCHMARK_CONFIG="${JB_BENCHMARK_CONFIG:-}"     # bare name | repo-relative | absolute; empty = generated read mix
JB_COMPARE_CONFIG="${JB_COMPARE_CONFIG:-config/compare/defaults.yaml}"
JB_RPS="${JB_RPS:-}"
JB_DURATION="${JB_DURATION:-}"
JB_VUS="${JB_VUS:-}"
JB_SEED="${JB_SEED:-1}"                             # fixed request sequence; 0 = clock-seeded
JB_CONCURRENCY="${JB_CONCURRENCY:-5}"
JB_TIMEOUT="${JB_TIMEOUT:-30}"
JB_VALIDATE_SCHEMA="${JB_VALIDATE_SCHEMA:-false}"
JB_HTML_REPORT="${JB_HTML_REPORT:-true}"
JB_DEEP_CHECK="${JB_DEEP_CHECK:-false}"
JB_ETH_CALL_CORPUS="${JB_ETH_CALL_CORPUS:-false}"
JB_ETH_CALL_CORPUS_FILE="${JB_ETH_CALL_CORPUS_FILE:-${CORPUS_DIR:-/data/expb-data/rpc-bench}/eth-call-corpus.jsonl.gz}"
JB_FAIL_ON_DIFF="${JB_FAIL_ON_DIFF:-false}"
JB_MAX_FAIL_RATE_PCT="${JB_MAX_FAIL_RATE_PCT:-1}"   # k6 exits 0 even at 100% failures
JB_EXTRA_ARGS="${JB_EXTRA_ARGS:-}"
CONTAINER_NAME="${JB_CONTAINER_NAME:-jsonbench-bench}"

[[ -n "$JB_MODE" ]] || JB_MODE="$([[ -n "$REFERENCE_RPC_URL" ]] && echo compare || echo benchmark)"
case "$JB_MODE" in
  benchmark) ;;
  compare) [[ -n "$REFERENCE_RPC_URL" ]] || die "JB_MODE=compare needs a reference node" ;;
  *) die "unknown JB_MODE '$JB_MODE' (expected benchmark | compare)" ;;
esac
case "$JB_ETH_CALL_CORPUS" in
  true|false) ;;
  *) die "JB_ETH_CALL_CORPUS must be true or false" ;;
esac
[[ "$JB_SEED" =~ ^[0-9]+$ ]] || die "JB_SEED must be a non-negative integer, got '$JB_SEED'"
if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
  [[ "$JB_MODE" == "benchmark" ]] || die "eth_call corpus is supported only in benchmark mode"
  [[ -f "$JB_ETH_CALL_CORPUS_FILE" ]] || die "eth_call corpus file not found: $JB_ETH_CALL_CORPUS_FILE"
  python3 "$HERE/corpus_parity.py" validate --corpus "$JB_ETH_CALL_CORPUS_FILE" || die "eth_call corpus failed validation"
  JB_DEEP_CHECK="false"
  JB_HTML_REPORT="false"
fi

mkdir -p "$OUT_DIR"
SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")"
assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
work="$SCRATCH_ROOT/jsonbench"
as_root rm -rf "$work"
mkdir -p "$work/io/out"

log "Cloning $JB_REPO@$JB_REF..."
git init -q "$work/src"
git -C "$work/src" remote add origin "$JB_REPO"
git -C "$work/src" fetch -q --depth 1 origin "$JB_REF" || die "failed to fetch $JB_REF from $JB_REPO"
git -C "$work/src" checkout -q FETCH_HEAD
runner_dockerfile="$work/src/runner/Dockerfile"
[[ -f "$runner_dockerfile" ]] || die "json-bench runner Dockerfile not found at $runner_dockerfile"
tag_ref="${JB_REF//[^a-zA-Z0-9_.-]/-}"
image_tag="jsonbench-runner:${tag_ref:0:24}"
log "Building $image_tag from runner/Dockerfile..."
docker build -q -f "$runner_dockerfile" -t "$image_tag" "$work/src" >/dev/null || die "failed to build the json-bench runner image"

clients_yaml="$work/io/clients.yaml"
{
  echo "clients:"
  for pair in "$LABEL|$CLIENT_TYPE|$RPC_URL" ${REFERENCE_RPC_URL:+"$REFERENCE_LABEL|$REFERENCE_CLIENT_TYPE|$REFERENCE_RPC_URL"}; do
    IFS='|' read -r n t u <<< "$pair"
    printf '  - name: "%s"\n    type: "%s"\n    url: "%s"\n    timeout: "60s"\n    max_retries: 3\n' "$n" "$t" "$u"
  done
} > "$clients_yaml"
log "Client registry:"
sed 's/^/  /' "$clients_yaml"

# json-bench's SafeReadPath rejects absolute paths: configs must be relative to the /jb checkout.
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

if [[ "$JB_MODE" == "benchmark" ]]; then
  need_pyyaml
  src_bench=""
  if [[ -n "$JB_BENCHMARK_CONFIG" ]]; then
    case "$JB_BENCHMARK_CONFIG" in
      /*)  src_bench="$JB_BENCHMARK_CONFIG" ;;
      */*) src_bench="$work/src/$JB_BENCHMARK_CONFIG" ;;
      *)   src_bench="$work/src/config/benchmark/${JB_BENCHMARK_CONFIG}.yaml" ;;
    esac
    [[ -f "$src_bench" ]] || die "benchmark_config '$JB_BENCHMARK_CONFIG' not found (looked at $src_bench)"
  fi
  corpus_dir=""
  if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
    corpus_dir="$work/src/rpc-calls/corpus"
    mkdir -p "$corpus_dir"
    log "Splitting eth_call corpus $(basename "$JB_ETH_CALL_CORPUS_FILE") into selector-class fixtures (contents stay on this machine)..."
    python3 "$HERE/prepare-eth-call-corpus.py" "$JB_ETH_CALL_CORPUS_FILE" "$corpus_dir" || die "failed to convert the eth_call corpus"
  fi
  JB_SRC_BENCH="$src_bench" JB_PRIMARY_LABEL="$LABEL" JB_REF_LABEL="${REFERENCE_RPC_URL:+$REFERENCE_LABEL}" \
  JB_CORPUS_CLASSES="${corpus_dir:+$corpus_dir/classes.json}" \
  python3 - "$work/io/benchmark.yaml" <<'PY'
import json, os, sys, yaml

out = sys.argv[1]
env = os.environ.get
src = env("JB_SRC_BENCH", "")
threshold = ["p(99)<600000"]  # never trips; makes k6 emit a per-call sub-metric
if src:
    with open(src) as f:
        cfg = yaml.safe_load(f) or {}
else:
    cfg = {
        "test_name": "RPC read benchmark",
        "description": "Snapshot-backed read-path benchmark",
        "duration": "60s", "rps": 100, "vus": 10,
        "calls": [
            {"name": "WETH balance eth_call", "method": "eth_call", "weight": 40,
             "params": [{"to": "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2",
                         "data": "0x70a08231000000000000000000000000000000000000000000000000000000000000000a"}]},
            {"name": "eth_getBalance", "method": "eth_getBalance", "weight": 20,
             "params": ["0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045", "latest"]},
            {"name": "eth_blockNumber", "method": "eth_blockNumber", "weight": 20, "params": []},
            {"name": "eth_getTransactionCount", "method": "eth_getTransactionCount", "weight": 10,
             "params": ["0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045", "latest"]},
            {"name": "eth_getBlockByNumber", "method": "eth_getBlockByNumber", "weight": 10,
             "params": ["latest", False]},
        ],
    }
cfg["clients"] = [env("JB_PRIMARY_LABEL")] + ([env("JB_REF_LABEL")] if env("JB_REF_LABEL") else [])
for key in ("rps", "vus", "seed"):
    if env("JB_" + key.upper(), "").strip():
        cfg[key] = int(env("JB_" + key.upper()))
if env("JB_DURATION", "").strip():
    cfg["duration"] = env("JB_DURATION")
if env("JB_CORPUS_CLASSES"):
    with open(env("JB_CORPUS_CLASSES")) as f:
        classes = json.load(f)
    cfg["calls"] = [{"name": name, "file": f"./rpc-calls/corpus/{name}.json", "file_type": "json", "weight": count}
                    for name, count in classes.items()]
for call in cfg.get("calls", []) or []:
    call.setdefault("thresholds", threshold)
with open(out, "w") as f:
    yaml.safe_dump(cfg, f, sort_keys=False, default_flow_style=False)
PY
  log "Rendered benchmark config (${JB_BENCHMARK_CONFIG:-generated default}) -> clients=[$LABEL${REFERENCE_RPC_URL:+, $REFERENCE_LABEL}] seed=${JB_SEED}"
  bench_cfg="/io/benchmark.yaml"
fi

chmod -R a+rwX "$work/io"   # the runner image runs as uid 1001
read -ra extra_args_arr <<< "$JB_EXTRA_ARGS"
docker_common=(--rm --name "$CONTAINER_NAME" --network host -w /jb -v "$work/src:/jb:ro" -v "$work/io:/io")
docker rm -fv "$CONTAINER_NAME" >/dev/null 2>&1 || true

# Resource sampling brackets container execution only.
sampler_pid=""
if [[ -n "${RESOURCE_SAMPLER_CONTAINER:-}" && -n "${RESOURCE_SAMPLER_OUT:-}" ]]; then
  python3 "$HERE/sample-resources.py" sample --container "$RESOURCE_SAMPLER_CONTAINER" --out "$RESOURCE_SAMPLER_OUT" &
  sampler_pid=$!
fi
stop_resource_sampler() {
  [[ -n "$sampler_pid" ]] || return 0
  kill -TERM "$sampler_pid" 2>/dev/null
  wait "$sampler_pid" 2>/dev/null
  sampler_pid=""
}
trap stop_resource_sampler EXIT

tool_failed=0
if [[ "$JB_MODE" == "compare" ]]; then
  assert_same_head "$RPC_URL" "$REFERENCE_RPC_URL"
  compare_cfg="$(resolve_config "$JB_COMPARE_CONFIG")"
  validate=()
  [[ "$JB_VALIDATE_SCHEMA" == "true" ]] && validate=(--validate-schema)
  log "json-bench compare: $LABEL vs $REFERENCE_LABEL (config: $JB_COMPARE_CONFIG)..."
  docker run "${docker_common[@]}" "$image_tag" compare \
    --config "$compare_cfg" --clients /io/clients.yaml --client-refs "$LABEL,$REFERENCE_LABEL" \
    --concurrency "$JB_CONCURRENCY" --timeout "$JB_TIMEOUT" --output /io/out \
    ${validate[@]+"${validate[@]}"} ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/jsonbench.log" \
    || tool_failed=1
elif [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
  # Tool output may echo call contents: it stays in scratch, not the job log.
  log "json-bench benchmark (private corpus)..."
  docker run "${docker_common[@]}" "$image_tag" benchmark \
    --config "$bench_cfg" --clients /io/clients.yaml --output /io/out \
    ${extra_args_arr[@]+"${extra_args_arr[@]}"} > "$work/jsonbench-tool.log" 2>&1 || tool_failed=1
  rm -rf "$corpus_dir"
  [[ "$tool_failed" == "0" ]] || die "json-bench exited non-zero — tool log retained on the runner at $work/jsonbench-tool.log"
else
  html=()
  [[ "$JB_HTML_REPORT" == "true" ]] && html=(--html-report)
  log "json-bench benchmark (config: ${JB_BENCHMARK_CONFIG:-<generated default>})..."
  docker run "${docker_common[@]}" "$image_tag" benchmark \
    --config "$bench_cfg" --clients /io/clients.yaml --output /io/out \
    ${html[@]+"${html[@]}"} ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/jsonbench.log" \
    || tool_failed=1
fi
stop_resource_sampler

if [[ "$JB_DEEP_CHECK" == "true" && "$JB_MODE" == "benchmark" ]]; then
  dc_out="$OUT_DIR/deep-check-$LABEL.jsonl"
  log "Deep-check: replaying every workload request once (client=$LABEL) -> $(basename "$dc_out")..."
  JB_RPC_URL="$RPC_URL" JB_SRC="$work/src" python3 - "$work/io/benchmark.yaml" "$dc_out" <<'PY' || log "::warning::deep-check capture failed (continuing)"
import os, sys, json, hashlib, urllib.request, yaml
cfg_path, out_path = sys.argv[1], sys.argv[2]
rpc, src = os.environ["JB_RPC_URL"], os.environ["JB_SRC"]
with open(cfg_path) as f:
    cfg = yaml.safe_load(f) or {}
reqs = []
for call in cfg.get("calls", []) or []:
    if call.get("file"):
        with open(os.path.join(src, call["file"].lstrip("./"))) as jf:
            for line in jf:
                if line.strip():
                    o = json.loads(line)
                    reqs.append((o.get("method"), o.get("params", [])))
    else:
        reqs.append((call.get("method"), call.get("params", [])))
def post(method, params):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    req = urllib.request.Request(rpc, data=body, headers={"content-type": "application/json"})
    with urllib.request.urlopen(req, timeout=90) as r:
        return json.loads(r.read())
with open(out_path, "w") as out:
    for n, (method, params) in enumerate(reqs):
        fp = hashlib.sha256(json.dumps([method, params], sort_keys=True, default=str).encode()).hexdigest()[:16]
        try:
            resp = post(method, params)
        except Exception as e:
            resp = {"_capture_error": str(e)}
        out.write(json.dumps({"seq": n, "fp": fp, "method": method, "response": resp}) + "\n")
print(f"deep-check: captured {len(reqs)} responses -> {out_path}")
PY
  chmod a+rw "$dc_out" 2>/dev/null || true
fi

as_root chown -R "$(id -u):$(id -g)" "$work/io" 2>/dev/null || true
if [[ -d "$work/io/out" ]]; then
  if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
    rm -f "$OUT_DIR/summary.json"
    python3 "$HERE/corpus_results.py" sanitize "$work/io/out/summary.json" "$OUT_DIR/summary.json" \
      || die "corpus run produced no valid aggregate summary — raw output retained on the runner under $work/io/out"
  else
    cp -r "$work/io/out/." "$OUT_DIR/" 2>/dev/null || true
  fi
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
      total="$(jq -r 'if type == "array" then . else (.results // .comparisons // []) end | length' "$results" 2>/dev/null || echo "?")"
      diff_count="$(jq -r '[ (if type == "array" then . else (.results // .comparisons // []) end)[] | select((.differences // {}) | length > 0) ] | length' "$results" 2>/dev/null || echo "")"
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
  bench_meta="$work/io/benchmark.yaml"
  disp_dur="$(sed -nE 's/^duration:[[:space:]]*"?([^"#]*)"?[[:space:]]*$/\1/p' "$bench_meta" 2>/dev/null | head -1)"
  disp_rps="$(sed -nE 's/^rps:[[:space:]]*([0-9]+).*/\1/p' "$bench_meta" 2>/dev/null | head -1)"
  disp_vus="$(sed -nE 's/^vus:[[:space:]]*([0-9]+).*/\1/p' "$bench_meta" 2>/dev/null | head -1)"

  perf_md="$OUT_DIR/.jsonbench-perf.md"
  : > "$perf_md"
  summary_parse_failed=0
  fail_pct=""
  if [[ -s "$OUT_DIR/summary.json" ]]; then
    fail_pct="$(python3 - "$OUT_DIR/summary.json" "$perf_md" <<'PY'
import json, re, sys
with open(sys.argv[1]) as f:
    metrics = (json.load(f) or {}).get("metrics", {}) or {}
def num(m, k):
    if not isinstance(m, dict):
        return 0.0
    v = m.get(k)
    if not isinstance(v, (int, float)):
        v = (m.get("values") or {}).get(k)
    return float(v) if isinstance(v, (int, float)) else 0.0
d, r, fail, chk = (metrics.get(k, {}) for k in ("http_req_duration", "http_reqs", "http_req_failed", "checks"))
fail_rate = num(fail, "rate") or num(fail, "value")
cp, cf = num(chk, "passes"), num(chk, "fails")
out = ["### Overall", "", "| metric | value |", "|---|---:|",
       "| requests | %d |" % int(num(r, "count")),
       "| throughput (req/s) | %.2f |" % num(r, "rate"),
       "| http fail rate | %.2f%% |" % (fail_rate * 100)]
if cp + cf > 0:
    out.append("| checks passed | %.2f%% |" % (cp / (cp + cf) * 100))
for label, key in (("avg", "avg"), ("p50", "med"), ("p90", "p(90)"), ("p95", "p(95)"), ("p99", "p(99)"), ("max", "max")):
    out.append("| latency %s (ms) | %.2f |" % (label, num(d, key)))
rows = []
for key, val in metrics.items():
    m = re.search(r"req_name:([^,}]+)", key) if key.startswith("http_req_duration{") else None
    if m:
        rows.append((m.group(1).strip().strip("'\""), val))
classes = metrics.get("classes") if isinstance(metrics.get("classes"), dict) else {}
rows += [(name, val) for name, val in classes.items()]
if rows:
    out += ["", "### Per method (http_req_duration, ms)", "", "| method | avg | p50 | p90 | p95 | p99 | max |", "|---|---:|---:|---:|---:|---:|---:|"]
    for name, val in sorted(rows, key=lambda x: x[0]):
        out.append("| %s | %.2f | %.2f | %.2f | %.2f | %.2f | %.2f |" % (
            name, num(val, "avg"), num(val, "med"), num(val, "p(90)"), num(val, "p(95)"), num(val, "p(99)"), num(val, "max")))
with open(sys.argv[2], "w") as f:
    f.write("\n".join(out) + "\n")
print("%.4f" % (fail_rate * 100))
PY
)" || { summary_parse_failed=1; fail_pct=""; }
  fi

  if [[ -n "${RESOURCE_SAMPLER_OUT:-}" && -s "$RESOURCE_SAMPLER_OUT" ]]; then
    delivered="$(json_number "$OUT_DIR/summary.json" '.metrics.http_reqs.values.count' 0)"
    if [[ "$delivered" =~ ^[0-9]+$ && "$delivered" -gt 0 ]]; then
      python3 "$HERE/sample-resources.py" normalize --out "$RESOURCE_SAMPLER_OUT" --requests "$delivered" || true
    else
      log "resource sample left un-normalized: no usable http_reqs count"
    fi
  fi

  {
    echo "## RPC Benchmark — json-bench (k6)"
    echo
    echo "Node(s): \`$LABEL\` = \`$RPC_URL\`${REFERENCE_RPC_URL:+, \`$REFERENCE_LABEL\` = \`$REFERENCE_RPC_URL\`} | config: \`${JB_BENCHMARK_CONFIG:-<generated default>}\` | duration: \`${disp_dur:-?}\` | rps: \`${disp_rps:-?}\` | vus: \`${disp_vus:-?}\` | seed: \`${JB_SEED}\`"
    echo
    [[ -s "$perf_md" ]] && { cat "$perf_md"; echo; }
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
    if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
      echo "Private corpus cell: aggregate-only \`summary.json\` in the artifact; raw tool output stays on the runner."
    elif [[ -s "$perf_md" || -s "$OUT_DIR/results.csv" ]]; then
      echo "Full results: \`summary.json\` / \`results.json\` / \`results.csv\`$([[ "$JB_HTML_REPORT" == "true" && -s "$OUT_DIR/report.html" ]] && echo " / \`report.html\`") in the artifact."
    else
      echo "**NO RESULTS** — json-bench wrote neither \`summary.json\` nor \`results.csv\` (see \`jsonbench.log\` in the artifact)."
    fi
    echo
  } > "$summary"
  rm -f "$perf_md"
fi
log "json-bench summary written to $summary"

[[ "$tool_failed" == "0" ]] || die "json-bench exited non-zero (see jsonbench.log)"
if [[ "$JB_MODE" == "benchmark" ]]; then
  [[ -s "$OUT_DIR/summary.json" || -s "$OUT_DIR/results.csv" ]] || die "json-bench benchmark produced no summary.json or results.csv"
  [[ ! -s "$OUT_DIR/summary.json" || "$summary_parse_failed" == "0" ]] || die "summary.json exists but could not be parsed"
  if [[ -n "$fail_pct" ]] && awk -v f="$fail_pct" -v m="$JB_MAX_FAIL_RATE_PCT" 'BEGIN { exit !(f > m) }'; then
    die "http fail rate ${fail_pct}% exceeds max_fail_rate_pct=${JB_MAX_FAIL_RATE_PCT}%"
  fi
else
  [[ -n "$diff_count" ]] || die "json-bench compare produced no parseable results"
  [[ "$JB_FAIL_ON_DIFF" != "true" || "$diff_count" == "0" ]] || die "json-bench compare found $diff_count response difference(s) and fail_on_diff is enabled"
fi
