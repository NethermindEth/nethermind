#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Run json-bench (NethermindEth/json-bench) against already-running JSON-RPC node(s):
# 'benchmark' (k6 load, metrics from summary.json) or 'compare' (differential diff). Knobs from JB_* env.

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
# Pin the commit so a default-branch push can't change results or run unreviewed
# code on the runner. Override JB_REF (sha/tag/branch) or JB_REPO to move it.
JB_REF="${JB_REF:-89c65c73f4325e8b6e1de2c520690bf468eb6c52}"
JB_MODE="${JB_MODE:-}"                       # benchmark | compare; empty = auto
# Benchmark workload: bare name -> config/benchmark/<name>.yaml, '/' = repo-relative,
# absolute = as-is; empty = generated default read mix (client list is rewritten here).
JB_BENCHMARK_CONFIG="${JB_BENCHMARK_CONFIG:-}"
JB_COMPARE_CONFIG="${JB_COMPARE_CONFIG:-config/compare/defaults.yaml}"
JB_RPS="${JB_RPS:-}"                         # override the workload's rps; empty = keep it (generated default: 100)
JB_DURATION="${JB_DURATION:-}"               # override the workload's k6 duration; empty = keep it (generated default: 60s)
JB_VUS="${JB_VUS:-}"                         # override the workload's vus; empty = keep it (generated default: 10)
JB_CONCURRENCY="${JB_CONCURRENCY:-5}"        # compare mode
JB_TIMEOUT="${JB_TIMEOUT:-30}"               # compare mode, per-request seconds
JB_VALIDATE_SCHEMA="${JB_VALIDATE_SCHEMA:-false}"
JB_HTML_REPORT="${JB_HTML_REPORT:-true}"
# Deep-check: after the timed load, replay each request once and store raw responses
# (deep-check-<label>.jsonl) for offline cross-client diffing (k6 checks only verify presence). Off; benchmark only.
JB_DEEP_CHECK="${JB_DEEP_CHECK:-false}"
# Private eth_call corpus (benchmark only): replace the workload's calls with a runner-side
# JSONL(.gz) corpus of {"method":"eth_call","params":[...]} records. Call contents stay on this
# machine: raw tool output goes to VM scratch instead of the job log, per-call outputs are not
# copied to OUT_DIR, and only a sanitized aggregate summary.json is published.
JB_ETH_CALL_CORPUS="${JB_ETH_CALL_CORPUS:-false}"
JB_ETH_CALL_CORPUS_FILE="${JB_ETH_CALL_CORPUS_FILE:-/mnt/sda/expb-data/rpc-bench/eth-call-corpus.jsonl.gz}"
# Response differences are reported (and warned about) by default; opt in to
# failing the step on any diff once the method set is curated for the clients.
JB_FAIL_ON_DIFF="${JB_FAIL_ON_DIFF:-false}"
# k6 exits 0 even at 100% HTTP failure (thresholds are loose by design), so the step
# fails itself when the summary.json fail rate exceeds this percentage.
JB_MAX_FAIL_RATE_PCT="${JB_MAX_FAIL_RATE_PCT:-1}"
JB_EXTRA_ARGS="${JB_EXTRA_ARGS:-}"
CONTAINER_NAME="${JB_CONTAINER_NAME:-jsonbench-bench}"
# Set only by the private corpus sweep for a measured k6 cell. The complete fixed event list
# lives here rather than accepting an event string from workflow input.
PERF_STAT_CONTAINER="${PERF_STAT_CONTAINER:-}"
PERF_STAT_OUT="${PERF_STAT_OUT:-}"
PERF_STAT_EVENTS=(
  task-clock cycles ref-cycles instructions cache-references cache-misses
  LLC-loads LLC-load-misses dTLB-loads dTLB-load-misses minor-faults page-faults
  context-switches cpu-migrations
)
perf_stat_enabled="false"
perf_finalized="false"
perf_pid=""
perf_launcher_pid=""
perf_pid_file=""
perf_start_time=""
perf_start_time_file=""
perf_raw=""
perf_stderr=""
perf_bin=""
perf_int_sent="false"
perf_signal_result=""

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
case "$JB_ETH_CALL_CORPUS" in
  true|false) ;;
  *) die "JB_ETH_CALL_CORPUS must be true or false" ;;
esac
if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
  [[ "$JB_MODE" == "benchmark" ]] || die "eth_call corpus is supported only in benchmark mode"
  [[ -f "$JB_ETH_CALL_CORPUS_FILE" ]] || die "eth_call corpus file not found: $JB_ETH_CALL_CORPUS_FILE"
  # The sweep validates corpora before starting a node; this path is entered directly, so apply the
  # same gate here. Without it a corpus the sweep rejects in seconds is converted into a multi-GB
  # fixture and handed to k6. corpus_parity is the single authority on what a legal corpus is.
  python3 "$HERE/corpus_parity.py" validate --corpus "$JB_ETH_CALL_CORPUS_FILE" \
    || die "eth_call corpus failed validation (see error above — it reports counts and line numbers, not contents)"
  # Both would write raw request/response content into OUT_DIR — keep the artifact aggregate-only.
  JB_DEEP_CHECK="false"
  JB_HTML_REPORT="false"
fi
if [[ -n "$PERF_STAT_CONTAINER" || -n "$PERF_STAT_OUT" ]]; then
  [[ -n "$PERF_STAT_CONTAINER" && -n "$PERF_STAT_OUT" ]] \
    || die "perf stat requires both the node container and output values"
  [[ "$JB_MODE" == "benchmark" && "$JB_ETH_CALL_CORPUS" == "true" ]] \
    || die "perf stat is supported only for private eth_call corpus benchmarks"
  [[ "$PERF_STAT_OUT" == "$OUT_DIR/perf-stat.json" ]] \
    || die "perf stat output must be the private corpus cell perf-stat.json"
  perf_bin="$(command -v perf 2>/dev/null)" || die "perf stat is not available on this host"
  rm -f -- "$PERF_STAT_OUT"
  perf_stat_enabled="true"
fi

mkdir -p "$OUT_DIR"
SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")"
assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
work="$SCRATCH_ROOT/jsonbench"
# The runner container may have left non-owner files in scratch on a prior run.
as_root rm -rf "$work"
mkdir -p "$work/io/out"

# Fetch the tool source and build the runner image (bundles k6).
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

# Render the client registry (and, for benchmark mode, the default workload).
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

# SafeReadPath rejects absolute paths and '..', so --config must stay relative to the
# container's /jb checkout; an absolute host path is copied in and passed by relative name.
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
  # Default read mix targeting OUR registry names. Loose per-call thresholds never trip;
  # they only make k6 emit a per-method http_req_duration sub-metric into summary.json.
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
  # Adapt a curated config: rewrite its client list to our node(s), inject loose per-call
  # thresholds (per-method sub-metrics), apply rps/vus/duration overrides. Fixtures stay relative.
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

# Fixtures stay relative: container CWD is /jb and json-bench's loader (SafeReadPath)
# rejects absolute paths, so ./rpc-calls/... resolve as-is.
for call in cfg.get("calls", []) or []:
    if not call.get("thresholds"):
        call["thresholds"] = ["p(99)<600000"]

with open(out, "w") as f:
    yaml.safe_dump(cfg, f, sort_keys=False, default_flow_style=False)
PY
  log "Adapted benchmark_config '$JB_BENCHMARK_CONFIG' -> clients=[$LABEL${ref_label:+, $ref_label}]"
  bench_cfg="/io/benchmark.yaml"
fi

if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
  # Convert the corpus into a JSON-array fixture inside the checkout (json-bench's JSONL reader
  # has a ~64 KiB scanner token limit; real eth_call records exceed it) and make it the only call.
  python3 -c 'import yaml' 2>/dev/null \
    || python3 -m pip install --user pyyaml 2>/dev/null \
    || python3 -m pip install --user --break-system-packages pyyaml \
    || die "PyYAML is required to prepare the eth_call corpus workload and could not be installed"
  corpus_fixture="$work/src/rpc-calls/runner-eth-call-corpus.json"
  mkdir -p "$work/src/rpc-calls"
  log "Preparing eth_call corpus fixture from $(basename "$JB_ETH_CALL_CORPUS_FILE") (contents stay on this machine)..."
  python3 "$HERE/prepare-eth-call-corpus.py" "$JB_ETH_CALL_CORPUS_FILE" "$corpus_fixture" \
    || die "failed to convert the eth_call corpus (see converter error above — it names line numbers, not contents)"
  python3 - "$work/io/benchmark.yaml" <<'PY'
import sys, yaml

path = sys.argv[1]
with open(path) as f:
    cfg = yaml.safe_load(f) or {}
cfg["calls"] = [{
    "name": "eth_call corpus",
    "file": "./rpc-calls/runner-eth-call-corpus.json",
    "file_type": "json",
    "weight": 1,
    "thresholds": ["p(99)<600000"],
}]
with open(path, "w") as f:
    yaml.safe_dump(cfg, f, sort_keys=False, default_flow_style=False)
PY
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
docker rm -fv "$CONTAINER_NAME" >/dev/null 2>&1 || true

# Resource sampling brackets container execution only. Cloning and building json-bench, converting
# the corpus fixture and post-processing the summary all happen outside this window, so they cannot
# dilute the wall-clock-derived figures (averages, peak cores).
sampler_pid=""
if [[ -n "${RESOURCE_SAMPLER_CONTAINER:-}" && -n "${RESOURCE_SAMPLER_OUT:-}" ]]; then
  python3 "$HERE/sample-resources.py" sample \
    --container "$RESOURCE_SAMPLER_CONTAINER" --out "$RESOURCE_SAMPLER_OUT" &
  sampler_pid=$!
fi
signal_resource_sampler_stop() {
  [[ -n "$sampler_pid" ]] || return 0
  kill -TERM "$sampler_pid" 2>/dev/null || ! kill -0 "$sampler_pid" 2>/dev/null
}

reap_resource_sampler() {
  local pid="$sampler_pid" sampler_failed=0
  [[ -n "$pid" ]] || return 0
  wait "$pid" 2>/dev/null || sampler_failed=1
  sampler_pid=""
  return "$sampler_failed"
}

start_perf_stat() {
  [[ "$perf_stat_enabled" == "true" ]] || return 0
  local node_pid event perf_arguments=()
  node_pid="$(docker inspect --format '{{.State.Pid}}' "$PERF_STAT_CONTAINER" 2>/dev/null)" \
    || { echo "ERROR: perf stat could not resolve the node container PID" >&2; return 1; }
  [[ "$node_pid" =~ ^[1-9][0-9]*$ ]] \
    || { echo "ERROR: perf stat received an invalid node container PID" >&2; return 1; }
  perf_raw="$work/perf-stat.raw.json"
  perf_stderr="$work/perf-stat.stderr"
  perf_pid_file="$work/perf-stat.pid"
  perf_start_time_file="$work/perf-stat.start-time"
  perf_pid=""
  perf_start_time=""
  perf_launcher_pid=""
  perf_int_sent="false"
  rm -f -- "$perf_raw" "$perf_stderr" "$perf_pid_file" "$perf_start_time_file"
  # Root perf preserves ownership when opening these pre-created runner-readable scratch files.
  : > "$perf_raw"; : > "$perf_stderr"; : > "$perf_pid_file"; : > "$perf_start_time_file"
  chmod 600 "$perf_raw" "$perf_stderr" "$perf_pid_file" "$perf_start_time_file"
  for event in "${PERF_STAT_EVENTS[@]}"; do perf_arguments+=(-e "$event"); done
  # The root shell records its PID and start time, then execs perf. The start time binds later root
  # signals to this process even when as_root is sudo and the host reuses the numeric PID.
  as_root bash -c '
    pid_file=$1
    raw=$2
    stderr_file=$3
    perf_binary=$4
    node_pid=$5
    start_time_file=$6
    shift 6
    perf_arguments=("$@")
    stat="$(<"/proc/$$/stat")" || exit 1
    stat="${stat##*) }"
    IFS=' '
    set -f
    set -- $stat
    start_time="${20:-}"
    case "$start_time" in ''|*[!0-9]*) exit 1 ;; esac
    printf "%s\n" "$$" > "$pid_file"
    printf "%s\n" "$start_time" > "$start_time_file"
    exec env LC_ALL=C LANG=C "$perf_binary" stat --json-output --no-big-num \
      --output "$raw" --pid "$node_pid" "${perf_arguments[@]}" >/dev/null 2>"$stderr_file"
  ' perf-stat-root-wrapper "$perf_pid_file" "$perf_raw" "$perf_stderr" "$perf_bin" "$node_pid" \
    "$perf_start_time_file" \
    "${perf_arguments[@]}" < /dev/null > /dev/null 2>&1 &
  perf_launcher_pid=$!
  wait_for_perf_start
}

perf_process_state() {
  local pid="$1"
  [[ "$pid" =~ ^[1-9][0-9]*$ && "$perf_start_time" =~ ^[0-9]+$ ]] || return 1
  as_root bash -c '
    pid=$1
    expected_start_time=$2
    stat="$(<"/proc/$pid/stat")" || exit 1
    stat="${stat##*) }"
    IFS=' '
    set -f
    set -- $stat
    state="${1:-}"
    start_time="${20:-}"
    case "$start_time" in ''|*[!0-9]*) exit 1 ;; esac
    [[ "$start_time" == "$expected_start_time" ]] || exit 1
    printf "%s\n" "$state"
  ' perf-stat-identity "$pid" "$perf_start_time" 2>/dev/null
}

perf_process_exists() {
  [[ -n "$(perf_process_state "$1")" ]]
}

signal_perf_process() {
  local signal="$1" pid="$2"
  perf_signal_result=""
  [[ "$pid" =~ ^[1-9][0-9]*$ && "$perf_start_time" =~ ^[0-9]+$ ]] || return 1
  case "$signal" in INT|TERM|KILL) ;; *) return 1 ;; esac
  perf_signal_result="$(as_root python3 "$HERE/corpus_results.py" perf-pidfd-signal \
    "$pid" "$perf_start_time" "$signal" 2>/dev/null)" || return 1
  [[ "$perf_signal_result" == "sent" || "$perf_signal_result" == "zombie" \
      || "$perf_signal_result" == "gone" ]]
}

wait_for_perf_start() {
  local attempts recorded_start_time recorded_pid
  for ((attempts = 0; attempts < 50; attempts++)); do
    if [[ -s "$perf_pid_file" && -s "$perf_start_time_file" ]]; then
      recorded_pid="$(<"$perf_pid_file")"
      recorded_start_time="$(<"$perf_start_time_file")"
      if [[ "$recorded_pid" =~ ^[1-9][0-9]*$ && "$recorded_start_time" =~ ^[0-9]+$ ]]; then
        perf_pid="$recorded_pid"
        perf_start_time="$recorded_start_time"
        if perf_process_exists "$perf_pid"; then
          return 0
        fi
      fi
      wait "$perf_launcher_pid" 2>/dev/null || true
      perf_launcher_pid=""
      return 1
    fi
    if ! kill -0 "$perf_launcher_pid" 2>/dev/null; then
      wait "$perf_launcher_pid" 2>/dev/null || true
      perf_launcher_pid=""
      return 1
    fi
    sleep 0.1
  done
  kill -TERM "$perf_launcher_pid" 2>/dev/null || true
  wait "$perf_launcher_pid" 2>/dev/null || true
  perf_launcher_pid=""
  return 1
}

wait_for_perf_exit() {
  local pid="$1" attempts state
  for ((attempts = 0; attempts < 50; attempts++)); do
    state="$(perf_process_state "$pid" 2>/dev/null || true)"
    [[ -n "$state" ]] || return 0
    [[ "$state" == Z* ]] && return 0
    sleep 0.1
  done
  return 1
}

signal_perf_stat_stop() {
  local pid="$perf_pid"
  [[ -n "$pid" ]] || return 0
  if perf_process_exists "$pid"; then
    if signal_perf_process INT "$pid"; then
      case "$perf_signal_result" in
        sent) perf_int_sent="true" ;;
        zombie|gone) ;;
        *) return 1 ;;
      esac
    else
      ! perf_process_exists "$pid"
    fi
  fi
}

reap_perf_stat() {
  local pid="$perf_pid" launcher="$perf_launcher_pid" launcher_status=0 stop_failed=0
  [[ -n "$pid" || -n "$launcher" ]] || return 0
  if [[ -n "$pid" ]] && perf_process_exists "$pid"; then
    if ! wait_for_perf_exit "$pid"; then
      signal_perf_process TERM "$pid" || { perf_process_exists "$pid" && stop_failed=1; }
      if ! wait_for_perf_exit "$pid"; then
        signal_perf_process KILL "$pid" || stop_failed=1
        wait_for_perf_exit "$pid" || stop_failed=1
      fi
    fi
  fi
  if [[ -n "$launcher" ]]; then
    if wait "$launcher" 2>/dev/null; then
      :
    else
      launcher_status=$?
      # perf re-raises the deliberate SIGINT through the root wrapper as status 130.
      if [[ "$launcher_status" -ne 130 || "$perf_int_sent" != "true" ]]; then
        stop_failed=1
      fi
    fi
  fi
  perf_pid=""
  perf_start_time=""
  perf_launcher_pid=""
  perf_int_sent="false"
  [[ -n "$perf_pid_file" ]] && rm -f -- "$perf_pid_file"
  [[ -n "$perf_start_time_file" ]] && rm -f -- "$perf_start_time_file"
  perf_pid_file=""
  perf_start_time_file=""
  return "$stop_failed"
}

cleanup_benchmark_window() {
  local status=$? cleanup_failed=0
  trap - EXIT
  set +e
  signal_perf_stat_stop || cleanup_failed=1
  signal_resource_sampler_stop || true
  reap_perf_stat || cleanup_failed=1
  reap_resource_sampler || true
  if [[ "$perf_stat_enabled" == "true" && "$perf_finalized" != "true" ]]; then
    rm -f -- "$PERF_STAT_OUT"
  fi
  [[ -n "$perf_raw" ]] && rm -f -- "$perf_raw"
  [[ -n "$perf_stderr" ]] && rm -f -- "$perf_stderr"
  [[ -n "$perf_pid_file" ]] && rm -f -- "$perf_pid_file"
  [[ -n "$perf_start_time_file" ]] && rm -f -- "$perf_start_time_file"
  [[ -n "${corpus_fixture:-}" ]] && rm -f -- "$corpus_fixture"
  if [[ "$status" -eq 0 && "$cleanup_failed" -ne 0 ]]; then status=1; fi
  exit "$status"
}
trap cleanup_benchmark_window EXIT

# Run the selected mode.
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
  # No --prometheus: json-bench builds per-client/per-method metrics from k6's
  # summary.json (which k6 writes anyway); per-call thresholds give the sub-metrics.
  html=()
  [[ "$JB_HTML_REPORT" == "true" ]] && html=(--html-report)
  log "json-bench benchmark (config: ${JB_BENCHMARK_CONFIG:-<generated default>}, summary.json metrics)..."
  if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
    # Tool output may echo call contents — keep it in VM scratch (wiped next run), not the job log.
    start_perf_stat || die "perf stat could not start for the private corpus cell"
    docker run "${docker_common[@]}" "$image_tag" \
      benchmark \
      --config "$bench_cfg" \
      --clients /io/clients.yaml \
      --output /io/out \
      ${extra_args_arr[@]+"${extra_args_arr[@]}"} > "$work/jsonbench-tool.log" 2>&1 \
      || tool_failed=1
    if [[ "$tool_failed" == "1" ]]; then
      die "json-bench exited non-zero — $(wc -l < "$work/jsonbench-tool.log" | tr -d ' ') tool log lines retained on the runner at $work/jsonbench-tool.log"
    fi
  else
    docker run "${docker_common[@]}" "$image_tag" \
      benchmark \
      --config "$bench_cfg" \
      --clients /io/clients.yaml \
      --output /io/out \
      ${html[@]+"${html[@]}"} \
      ${extra_args_arr[@]+"${extra_args_arr[@]}"} 2>&1 | tee "$OUT_DIR/jsonbench.log" \
      || tool_failed=1
  fi
fi
# Close the window before summary post-processing; signal both collectors before either reaps.
perf_stop_failed=0
signal_perf_stat_stop || perf_stop_failed=1
signal_resource_sampler_stop || true
reap_perf_stat || perf_stop_failed=1
reap_resource_sampler || true
if [[ "$perf_stop_failed" == "1" ]]; then
  [[ "$perf_stat_enabled" == "true" ]] && rm -f -- "$PERF_STAT_OUT"
  die "perf stat did not stop cleanly for the private corpus cell"
fi
[[ "$JB_ETH_CALL_CORPUS" == "true" ]] && rm -f -- "$corpus_fixture"

# Deep-check capture: replay each request once after the timed load (won't perturb k6),
# storing raw responses keyed by request fingerprint for offline cross-client diff. Non-fatal.
if [[ "$JB_DEEP_CHECK" == "true" && "$JB_MODE" == "benchmark" ]]; then
  dc_out="$OUT_DIR/deep-check-$LABEL.jsonl"
  log "Deep-check: capturing responses for every workload request (client=$LABEL) -> $(basename "$dc_out")..."
  # The capture imports PyYAML; the curated-config branch installs it, but the generated-default
  # branch never reaches that bootstrap — ensure it here (no-op if present, non-fatal if install fails).
  python3 -c 'import yaml' 2>/dev/null \
    || python3 -m pip install --user pyyaml 2>/dev/null \
    || python3 -m pip install --user --break-system-packages pyyaml 2>/dev/null || true
  JB_RPC_URL="$RPC_URL" JB_SRC="$work/src" \
  python3 - "$work/io/benchmark.yaml" "$dc_out" <<'PY' || log "::warning::deep-check capture failed (continuing)"
import os, sys, json, hashlib, urllib.request, yaml
cfg_path, out_path = sys.argv[1], sys.argv[2]
rpc, src = os.environ["JB_RPC_URL"], os.environ["JB_SRC"]
with open(cfg_path) as f:
    cfg = yaml.safe_load(f) or {}
reqs = []  # (method, params) in workload order
for call in cfg.get("calls", []) or []:
    fpath = call.get("file")
    if fpath:
        with open(os.path.join(src, fpath.lstrip("./"))) as jf:
            for line in jf:
                line = line.strip()
                if line:
                    o = json.loads(line)
                    reqs.append((o.get("method"), o.get("params", [])))
    else:
        reqs.append((call.get("method"), call.get("params", [])))
def post(method, params):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    req = urllib.request.Request(rpc, data=body, headers={"content-type": "application/json"})
    with urllib.request.urlopen(req, timeout=90) as r:
        return json.loads(r.read())
n = 0
with open(out_path, "w") as out:
    for method, params in reqs:
        fp = hashlib.sha256(json.dumps([method, params], sort_keys=True, default=str).encode()).hexdigest()[:16]
        try:
            resp = post(method, params)
        except Exception as e:
            resp = {"_capture_error": str(e)}
        out.write(json.dumps({"seq": n, "fp": fp, "method": method, "response": resp}) + "\n")
        n += 1
print(f"deep-check: captured {n} responses -> {out_path}")
PY
  chmod a+rw "$dc_out" 2>/dev/null || true
fi

# Collect outputs and build a markdown summary.
as_root chown -R "$(id -u):$(id -g)" "$work/io" 2>/dev/null || true
if [[ -d "$work/io/out" ]]; then
  if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
    # Publish only a sanitized fixed-schema aggregate; per-call k6 output stays in scratch.
    rm -f "$OUT_DIR/summary.json"
    python3 "$HERE/corpus_results.py" sanitize "$work/io/out/summary.json" "$OUT_DIR/summary.json" \
      || die "corpus run produced no valid aggregate summary — raw output retained on the runner under $work/io/out"
  else
    cp -r "$work/io/out/." "$OUT_DIR/" 2>/dev/null || true
  fi
fi
if [[ "$perf_stat_enabled" == "true" ]]; then
  [[ -s "$perf_raw" && -s "$OUT_DIR/summary.json" ]] \
    || die "perf stat produced no usable aggregate counter output"
  delivered="$(python3 - "$OUT_DIR/summary.json" <<'PY' 2>/dev/null || true
import json
import math
import sys

try:
    count = ((json.load(open(sys.argv[1], encoding="utf-8")) or {}).get("metrics", {})
             .get("http_reqs", {}).get("values", {}).get("count"))
    if isinstance(count, bool) or not isinstance(count, (int, float)) \
            or not math.isfinite(count) or count < 1 or int(count) != count:
        raise ValueError
    print(int(count))
except Exception:
    pass
PY
)"
  [[ "$delivered" =~ ^[1-9][0-9]*$ ]] \
    || die "perf stat could not determine the delivered request count"
  perf_temp="$(mktemp "$OUT_DIR/.perf-stat.XXXXXX")" \
    || die "perf stat could not prepare its aggregate output"
  if ! python3 "$HERE/corpus_results.py" perf-normalize "$perf_raw" "$perf_temp" "$delivered"; then
    rm -f -- "$perf_temp" "$PERF_STAT_OUT"
    die "perf stat output was malformed or missing required counters"
  fi
  if ! mv -f -- "$perf_temp" "$PERF_STAT_OUT"; then
    rm -f -- "$perf_temp" "$PERF_STAT_OUT"
    die "perf stat could not publish its aggregate output"
  fi
  perf_finalized="true"
  rm -f -- "$perf_raw" "$perf_stderr"
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

  # Parse k6's summary.json into overall + per-method tables and emit the http fail rate
  # for the gate; a parse failure is remembered so it fails the step, not silent-passes.
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

  # Per-request resource cost uses the count the load actually delivered. Deriving it from
  # rate x duration would assume an integer-second duration and that k6 dropped no iterations.
  if [[ -n "${RESOURCE_SAMPLER_OUT:-}" && -s "$RESOURCE_SAMPLER_OUT" && -s "$OUT_DIR/summary.json" ]]; then
    delivered="$(python3 - "$OUT_DIR/summary.json" <<'PY' 2>/dev/null || echo 0
import json, sys
try:
    metrics = (json.load(open(sys.argv[1])) or {}).get("metrics", {}) or {}
    count = ((metrics.get("http_reqs") or {}).get("values") or {}).get("count")
except Exception:
    count = None
print(int(count) if isinstance(count, (int, float)) and not isinstance(count, bool) and count > 0 else 0)
PY
)"
    if [[ "$delivered" =~ ^[0-9]+$ && "$delivered" -gt 0 ]]; then
      python3 "$HERE/sample-resources.py" normalize --out "$RESOURCE_SAMPLER_OUT" --requests "$delivered" || true
    else
      log "resource sample left un-normalized: no usable http_reqs count"
    fi
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
    if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
      echo "Private corpus cell: aggregate-only \`summary.json\` in the artifact; raw tool output stays on the runner."
    elif [[ -s "$perf_md" || -s "$OUT_DIR/results.csv" ]]; then
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
