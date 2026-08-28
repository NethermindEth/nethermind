#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Sweep one Nethermind image per CLIENTS entry over the same flat snapshot, one node at a time:
# json-bench cells per rps (isolated + mixed, or private eth_call corpus cells with parity/timings).

set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$here/lib.sh"

: "${OUT_DIR:?base output directory}"
: "${STATE_ROOT:?base per-client node state directory}"
: "${SCRATCH_ROOT:?writable scratch root on the snapshot disk}"
: "${NM_IMAGE:?built nethermind image ref}"
: "${SNAPSHOT_BLOCK:?shared head all clients are pinned to}"
: "${JB_BENCHMARK_CONFIG:?mixed (all-scenario) benchmark config, repo-relative}"

CLIENTS="${CLIENTS:-nethermind}"            # entries: ctype or ctype@image
ROUNDS="${ROUNDS:-1}"                       # >1 repeats CLIENTS, reversing order on even rounds (2 = ABBA)
RPS_LIST="${RPS_LIST-100 250 500}"          # explicitly empty = no k6 cells (parity/timings only)
ISO_CONFIGS="${ISO_CONFIGS:-}"
STATE_LAYOUT="${STATE_LAYOUT:-flat}"
JB_DURATION="${JB_DURATION:-60s}"
ISO_DURATION="${ISO_DURATION:-20s}"
JB_REF="${JB_REF:-}"
JB_SEED="${JB_SEED:-1}"
NETWORK="${NETWORK:-mainnet}"
JSONRPC_MODULES="${JSONRPC_MODULES:-Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-1800}"
DIAG_DIR="${DIAG_DIR:-$SCRATCH_ROOT/diag}"
JB_ETH_CALL_CORPUS="${JB_ETH_CALL_CORPUS:-false}"
CORPUS_DIR="${CORPUS_DIR:-/data/expb-data/rpc-bench}"
CORPUS_GLOB="${CORPUS_GLOB:-eth-call-corpus*.jsonl.gz}"
CORPUS_REQUESTS="${CORPUS_REQUESTS:-}"      # size a cell by request count (absolute) ...
CORPUS_PASSES="${CORPUS_PASSES:-}"          # ... or as a multiple of the corpus record count
CORPUS_TIMINGS_PASSES="${CORPUS_TIMINGS_PASSES:-}"   # per-record replay; 0 rps = closed loop at CORPUS_TIMINGS_CONCURRENCY
CORPUS_TIMINGS_RPS="${CORPUS_TIMINGS_RPS:-0}"
CORPUS_TIMINGS_CONCURRENCY="${CORPUS_TIMINGS_CONCURRENCY:-16}"
CORPUS_WARMUP_RPS_MAX="${CORPUS_WARMUP_RPS_MAX:-0}"   # 0 = no cap on the warm-up rate
CORPUS_PARITY_DIFFS="${CORPUS_PARITY_DIFFS:-false}"
CORPUS_RESOURCE_SAMPLING="${CORPUS_RESOURCE_SAMPLING:-true}"
CORPUS_WARMUP_DURATION="${CORPUS_WARMUP_DURATION:-60s}"   # discarded load per node per corpus; 0 = measure cold
CORPUS_WARMUP_RPS="${CORPUS_WARMUP_RPS:-400}"             # floor; a higher measured rate warms at that rate
CORPUS_BASELINE="${CORPUS_BASELINE:-none}"                # save: persist this run's parity baseline on the runner; use: compare against the saved one
CORPUS_BASELINE_DIR="${CORPUS_BASELINE_DIR:-$CORPUS_DIR/baselines}"
CORPUS_RPC_GAS_CAP="1000000000000"
DB_ISOLATION_ALL="${DB_ISOLATION_ALL:-}"
DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION="${DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION:-false}"
SNAPSHOT_ROOT="${SNAPSHOT_ROOT:-/data/nethermind}"
SNAPSHOT_PATH="${SNAPSHOT_ROOT}/nethermind-flat-${SNAPSHOT_BLOCK}"
NM_LAYOUT_FLAGS="--FlatDb.Enabled=true"
PARITY_STATE="$SCRATCH_ROOT/parity"
RPC="http://localhost:8545"

require_positive_int() {
  [[ -z "$2" || "$2" =~ ^[1-9][0-9]*$ ]] || { echo "::error::$1 must be a positive integer, got '$2'"; exit 1; }
}
require_positive_int ROUNDS "$ROUNDS"
require_positive_int CORPUS_REQUESTS "$CORPUS_REQUESTS"
require_positive_int CORPUS_PASSES "$CORPUS_PASSES"
require_positive_int CORPUS_TIMINGS_PASSES "$CORPUS_TIMINGS_PASSES"
require_positive_int CORPUS_TIMINGS_CONCURRENCY "$CORPUS_TIMINGS_CONCURRENCY"
require_positive_int CORPUS_WARMUP_RPS "$CORPUS_WARMUP_RPS"
for _rps in $RPS_LIST; do require_positive_int "rps_list entry" "$_rps"; done
[[ -z "$CORPUS_REQUESTS" || -z "$CORPUS_PASSES" ]] || { echo "::error::corpus_requests and corpus_passes are mutually exclusive"; exit 1; }
[[ "$CORPUS_TIMINGS_RPS" =~ ^[0-9]+$ ]] || { echo "::error::timings_rps must be a non-negative integer, got '$CORPUS_TIMINGS_RPS'"; exit 1; }
[[ "$CORPUS_WARMUP_RPS_MAX" =~ ^[0-9]+$ ]] || { echo "::error::corpus_warmup_rps_max must be a non-negative integer, got '$CORPUS_WARMUP_RPS_MAX'"; exit 1; }
[[ "$JB_SEED" =~ ^[0-9]+$ ]] || { echo "::error::seed must be a non-negative integer, got '$JB_SEED'"; exit 1; }
[[ "$CORPUS_WARMUP_DURATION" =~ ^[0-9]+s?$ ]] || { echo "::error::corpus_warmup_duration must be integer seconds, got '$CORPUS_WARMUP_DURATION'"; exit 1; }
WARMUP_SECONDS="${CORPUS_WARMUP_DURATION%s}"
WARMUP_SEED=$(( JB_SEED + 1000 ))   # a measured cell must never replay exactly the sequence the warm-up just ran
case "$JB_ETH_CALL_CORPUS" in
  true|false) ;;
  *) echo "::error::JB_ETH_CALL_CORPUS must be true or false"; exit 1 ;;
esac
case "$CORPUS_BASELINE" in
  none|save|use) ;;
  *) echo "::error::CORPUS_BASELINE must be none, save or use, got '$CORPUS_BASELINE'"; exit 1 ;;
esac
for entry in $CLIENTS; do
  entry="${entry%%#*}"
  [[ "${entry%%@*}" == "nethermind" ]] || { echo "::error::sweep mode resolves one Nethermind snapshot set; client '${entry%%@*}' cannot run here (use benchmark_tool=jsonbench with reference_client)"; exit 1; }
done
[[ "$STATE_LAYOUT" == "flat" ]] || { echo "::error::sweep mode resolves a flat snapshot; state_layout '$STATE_LAYOUT' cannot run here"; exit 1; }
# direct bind-mounts the expb-shared snapshot read-write; one such run replaces the fixture every later benchmark uses.
if [[ "$DB_ISOLATION_ALL" == "direct" && "$DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION" != "true" ]]; then
  echo "::error::DB_ISOLATION_ALL=direct mutates the shared snapshot; use 'copy', or set DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION=true on a private snapshot"; exit 1
fi
DB_ISOLATION="${DB_ISOLATION_ALL:-overlay}"

# $1 config $2 rps $3 duration $4 cell dir $5 ctype $6 label [$7 corpus file] [$8 node container to sample]
run_cell() {
  local corpus="${7:-}" node="${8:-}" sampler_container="" sampler_out=""
  mkdir -p "$4"
  [[ -n "$node" && "$CORPUS_RESOURCE_SAMPLING" == "true" ]] && { sampler_container="$node"; sampler_out="$4/resources.json"; }
  OUT_DIR="$4" RPC_URL="$RPC" CLIENT_TYPE="$5" LABEL="$6" SCRATCH_ROOT="$SCRATCH_ROOT" JB_REF="$JB_REF" JB_MODE="benchmark" \
    JB_BENCHMARK_CONFIG="$1" JB_RPS="$2" JB_DURATION="$3" JB_SEED="$JB_SEED" JB_HTML_REPORT="false" \
    JB_DEEP_CHECK="$([[ -n "$corpus" ]] && echo false || echo true)" \
    JB_ETH_CALL_CORPUS="$([[ -n "$corpus" ]] && echo true || echo false)" JB_ETH_CALL_CORPUS_FILE="$corpus" \
    RESOURCE_SAMPLER_CONTAINER="$sampler_container" RESOURCE_SAMPLER_OUT="$sampler_out" \
    "$here/run-jsonbench.sh"
}

# Percentiles above the failure rate describe failures, not latency — say so per cell.
report_fail_rate() {
  local rate
  rate="$(json_number "$1/summary.json" '.metrics.http_req_failed.values.rate * 100' "")"
  [[ -n "$rate" ]] || return 0
  if awk -v r="$rate" -v m="${JB_MAX_FAIL_RATE_PCT:-1}" 'BEGIN{exit !(r > m)}'; then
    echo "::warning::$2: ${rate}% of requests failed — percentiles at or above p$(awk -v r="$rate" 'BEGIN{printf "%d", 100-r}') describe failures, not latency"
  else
    echo "   $2: fail rate ${rate}%"
  fi
}

corpus_cell_duration() {   # $1 corpus $2 rps
  local target=""
  if [[ -n "$CORPUS_REQUESTS" ]]; then target="$CORPUS_REQUESTS"
  elif [[ -n "$CORPUS_PASSES" ]]; then target=$(( CORPUS_RECORDS[$1] * CORPUS_PASSES ))
  else printf '%s' "$JB_DURATION"; return; fi
  printf '%ss' "$(( (target + $2 - 1) / $2 ))"
}

corpus_label() {   # eth-call-corpus[-<label>].jsonl.gz -> <label> | default
  local b; b="$(basename "$1")"
  b="${b#eth-call-corpus}"; b="${b#-}"; b="${b%.jsonl.gz}"
  printf '%s' "${b:-default}" | tr -c 'a-zA-Z0-9._\n' '-'
}

# Discarded load before a node's measured cells (a cold node fails ~2% and reads ~60% high on p99).
# Sets WARMED_SECONDS/WARMED_RPS to what was delivered. $1 clabel $2 label $3 corpus $4 ctype
warm_node() {
  WARMED_SECONDS=0; WARMED_RPS=0
  (( WARMUP_SECONDS > 0 )) || return 0
  local warm_rps="$CORPUS_WARMUP_RPS" r got
  for r in $RPS_LIST; do (( r > warm_rps )) && warm_rps=$r; done
  [[ -n "$CORPUS_TIMINGS_PASSES" ]] && (( CORPUS_TIMINGS_RPS > warm_rps )) && warm_rps="$CORPUS_TIMINGS_RPS"
  # json-bench pre-generates rps x duration request rows and k6 parses the whole file, so a long warm-up at a high
  # rate can exceed what the generator can hold; the cap trades warm-up pace for warm-up length.
  (( CORPUS_WARMUP_RPS_MAX > 0 && warm_rps > CORPUS_WARMUP_RPS_MAX )) && warm_rps="$CORPUS_WARMUP_RPS_MAX"
  local warm_cell="$SCRATCH_ROOT/warmup-cell/$1/$2"   # outside OUT_DIR so it is never staged
  echo "-- WARMUP $1 $2 @ rps=${warm_rps} for ${WARMUP_SECONDS}s (discarded) --"
  if [[ -n "$RPS_LIST" ]]; then
    if ! JB_MAX_FAIL_RATE_PCT=100 JB_SEED="$WARMUP_SEED" run_cell "$JB_BENCHMARK_CONFIG" "$warm_rps" "${WARMUP_SECONDS}s" "$warm_cell" "$4" "$2" "$3" ""; then
      echo "::warning::warmup for $2 failed — measured cells may be cold"; return 0
    fi
    WARMED_SECONDS="$WARMUP_SECONDS"
    got="$(json_number "$warm_cell/summary.json" '.metrics.http_reqs.values.count' 0)"
    if (( got > 0 )); then
      WARMED_RPS=$(( got / WARMUP_SECONDS ))
      (( got * 10 >= warm_rps * WARMUP_SECONDS * 8 )) || echo "::warning::warmup for $2 delivered ${got} of $(( warm_rps * WARMUP_SECONDS )) requests — cells may be under-warmed"
    else
      WARMED_RPS="$warm_rps"
      echo "::warning::warmup for $2: no usable http_reqs count — recorded warmup_rps is the requested pace"
    fi
    report_fail_rate "$warm_cell" "warmup $1/$2"
  else
    local records="${CORPUS_RECORDS[$3]}" started=$SECONDS bound=$(( WARMUP_SECONDS + 60 ))
    (( bound < 300 )) && bound=300
    mkdir -p "$warm_cell"
    timeout "$bound" python3 "$here/corpus_parity.py" timings --corpus "$3" --rpc-url "$RPC" \
      --out "$warm_cell/warmup-timings.csv" --passes "$(( (warm_rps * WARMUP_SECONDS + records - 1) / records ))" \
      --rps "$warm_rps" --concurrency "$CORPUS_TIMINGS_CONCURRENCY"
    local status=$?
    if [[ "$status" -eq 0 || "$status" -eq 124 ]]; then   # 124: the window elapsed under load, which is a completed warm-up
      WARMED_SECONDS=$(( SECONDS - started ))
      WARMED_RPS="$(json_number "$warm_cell/timings.meta.json" '.achieved_rps' "$warm_rps")"
    else
      echo "::warning::warmup replay for $2 failed — measured cells may be cold"
    fi
  fi
}

# $1 clabel $2 label $3 corpus $4 baseline state $5 baseline label $6 "saved" when the state came from CORPUS_BASELINE_DIR
parity_compare() {
  local report_dir="$OUT_DIR/corpus/$1/$2" report status
  mkdir -p "$report_dir"; report="$report_dir/parity.json"
  echo "-- PARITY $1: $2 vs ${6:+saved }baseline $5 --"
  python3 "$here/corpus_parity.py" compare --corpus "$3" --rpc-url "$RPC" --state "$4" \
    --report "$report" --baseline-client "$5" --candidate-client "$2" \
    $([[ "$CORPUS_PARITY_DIFFS" == "true" ]] && echo "--diffs $report_dir/parity-diffs.json")
  status=$?
  if (( status == 0 )); then
    PARITY_ROWS+=("$1|$2|$report")
  elif (( status == 2 )) && [[ -n "$6" ]]; then
    # Exit 2 is "the comparison could not run": unreadable saved state, a moved snapshot head/hash, an unreadable
    # corpus, or a node that did not answer. Only the snapshot case calls for re-recording, so do not advise it here.
    echo "::error::parity not checked for $2 on corpus $1 — compare could not run against the saved baseline $5 (see its error above); re-record the master baseline only if the snapshot moved"
    parity_skipped=$((parity_skipped + 1))
  else
    echo "::warning::parity defects for $2 vs $5 on corpus $1"; parity_fail=$((parity_fail + 1))
    [[ -f "$report" ]] && PARITY_ROWS+=("$1|$2|$report")
  fi
}

# $1 clabel $2 label $3 corpus $4 ctype $5 container
run_corpus() {
  local clabel="$1" label="$2" corpus="$3" ctype="$4" cname="$5" rps slot cell dur report_dir
  warm_node "$clabel" "$label" "$corpus" "$ctype"
  declare -A rps_seen=()
  for rps in $RPS_LIST; do
    rps_seen[$rps]=$(( ${rps_seen[$rps]:-0} + 1 ))
    slot="$rps"; (( rps_seen[$rps] > 1 )) && slot="${rps}_r${rps_seen[$rps]}"
    cell="$OUT_DIR/corpus/$clabel/$label/$slot"
    dur="$(corpus_cell_duration "$corpus" "$rps")"
    echo "-- CORPUS $clabel $label @ rps=$rps for $dur --"
    run_cell "$JB_BENCHMARK_CONFIG" "$rps" "$dur" "$cell" "$ctype" "$label" "$corpus" "$cname" \
      || { echo "::warning::corpus $clabel/$label/$slot failed"; cell_fail=$((cell_fail + 1)); }
    report_fail_rate "$cell" "$clabel/$label/$slot"
    [[ -f "$cell/jsonbench-summary.md" ]] && SUMMARIES+=("iso|$clabel|$label|$slot=$cell/jsonbench-summary.md")
  done
  local saved="$CORPUS_BASELINE_DIR/$clabel.json.gz"
  if [[ -n "${PARITY_BASE_STATE[$clabel]:-}" ]]; then
    parity_compare "$clabel" "$label" "$corpus" "${PARITY_BASE_STATE[$clabel]}" "${PARITY_BASE_LABEL[$clabel]}" "${PARITY_BASE_SAVED[$clabel]}"
  elif [[ "$CORPUS_BASELINE" == "use" && -s "$saved" ]]; then
    PARITY_BASE_STATE[$clabel]="$saved"; PARITY_BASE_SAVED[$clabel]="saved"
    PARITY_BASE_LABEL[$clabel]="$(cat "$CORPUS_BASELINE_DIR/$clabel.label" 2>/dev/null || echo master)"
    parity_compare "$clabel" "$label" "$corpus" "$saved" "${PARITY_BASE_LABEL[$clabel]}" "saved"
  else
    [[ "$CORPUS_BASELINE" != "use" ]] || echo "::warning::no saved parity baseline for corpus $clabel — $label becomes the baseline for this run"
    echo "-- PARITY $clabel: capturing baseline ($label) --"
    if python3 "$here/corpus_parity.py" baseline --corpus "$corpus" --rpc-url "$RPC" --state "$PARITY_STATE/$clabel.json"; then
      PARITY_BASE_STATE[$clabel]="$PARITY_STATE/$clabel.json"; PARITY_BASE_LABEL[$clabel]="$label"; PARITY_BASE_SAVED[$clabel]=""
      if [[ "$CORPUS_BASELINE" == "save" ]]; then
        # Rename into place: a run killed mid-copy must not leave a truncated state behind, since the read side
        # accepts any non-empty file and would then report "parity not checked" on every later run.
        if mkdir -p "$CORPUS_BASELINE_DIR" \
          && cp "$PARITY_STATE/$clabel.json" "$saved.tmp" && printf '%s\n' "$label" > "$CORPUS_BASELINE_DIR/$clabel.label.tmp" \
          && mv -f "$CORPUS_BASELINE_DIR/$clabel.label.tmp" "$CORPUS_BASELINE_DIR/$clabel.label" && mv -f "$saved.tmp" "$saved"; then
          echo "   saved parity baseline for $clabel -> $saved"
        else
          echo "::warning::could not save the parity baseline for $clabel under $CORPUS_BASELINE_DIR"
          rm -f "$saved.tmp" "$CORPUS_BASELINE_DIR/$clabel.label.tmp"
        fi
      fi
    else
      echo "::error::parity baseline capture failed for corpus $clabel on $label"; parity_fail=$((parity_fail + 1))
    fi
  fi
  if [[ -n "$CORPUS_TIMINGS_PASSES" ]]; then
    report_dir="$OUT_DIR/corpus/$clabel/$label"; mkdir -p "$report_dir"
    echo "-- TIMINGS $clabel: $label ($CORPUS_TIMINGS_PASSES passes @ $CORPUS_TIMINGS_RPS rps, concurrency $CORPUS_TIMINGS_CONCURRENCY) --"
    python3 "$here/corpus_parity.py" timings --corpus "$corpus" --rpc-url "$RPC" --out "$report_dir/timings.csv" \
        --passes "$CORPUS_TIMINGS_PASSES" --rps "$CORPUS_TIMINGS_RPS" --concurrency "$CORPUS_TIMINGS_CONCURRENCY" \
        --warmup-seconds "$WARMED_SECONDS" --warmup-rps "$WARMED_RPS" \
      || { echo "::warning::timings replay failed for $label on corpus $clabel"; cell_fail=$((cell_fail + 1)); }
  fi
}

# $1 label $2 ctype $3 log file. Corpus mode prints counts only and deletes the log (lines could quote call data).
scan_node_log() {
  [[ -f "$3" ]] || return 0
  local clean="$3.clean" show="true" exc pattern
  [[ "$JB_ETH_CALL_CORPUS" == "true" ]] && show="false"
  strip_ansi "$3" > "$clean"
  exc="$(grep -i "Exception" "$clean" | grep -vF 'Incorrect JSON RPC parameters' | wc -l | tr -d ' ')"
  if (( exc > 0 )); then
    if [[ "$2" == "nethermind" ]]; then echo "::warning::$1: $exc Exception line(s) in node log"; node_issue=1
    else echo "::warning::$1: $exc Exception-like line(s) in node log (warn only, non-Nethermind)"; fi
    [[ "$show" == "true" ]] && { grep -i "Exception" "$clean" | grep -vF 'Incorrect JSON RPC parameters' | head -20; }
  fi
  if [[ "$2" == "nethermind" ]]; then
    grep -qEi 'invalid[[:space:]_-]*block' "$clean" && { echo "::warning::$1: invalid block in node log"; node_issue=1; }
    grep -q "Nethermind is shut down" "$clean" || { echo "::warning::$1: 'Nethermind is shut down' marker missing — node did not shut down cleanly"; node_issue=1; }
  fi
  for pattern in Unhandled Fatal ERROR; do
    grep -qi "$pattern" "$clean" || continue
    echo "::warning::$1: severe log pattern '$pattern' ($(grep -ci "$pattern" "$clean") line(s))"
    [[ "$show" == "true" ]] && { grep -in "$pattern" "$clean" | head -10 || true; }
  done
  [[ "$show" == "true" ]] || rm -f "$3" "$clean"
}

mkdir -p "$OUT_DIR" "$STATE_ROOT"
declare -a SUMMARIES=() LABELS=() CORPORA=() PARITY_ROWS=()
declare -A CORPUS_RECORDS=() LABEL_SEEN=() PARITY_BASE_STATE=() PARITY_BASE_LABEL=() PARITY_BASE_SAVED=()
node_issue=0; cell_fail=0; stop_fail=0; parity_fail=0; parity_skipped=0; baseline_fail=0
BASELINE_LABEL=""
USING_SAVED_BASELINE=false   # true once every corpus has a saved baseline to compare against, so no arm here is one

if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
  for f in "$CORPUS_DIR"/$CORPUS_GLOB; do [[ -f "$f" ]] && CORPORA+=("$f"); done
  [[ "${#CORPORA[@]}" -gt 0 ]] || { echo "::error::no corpus files matching '$CORPUS_GLOB' under $CORPUS_DIR"; exit 1; }
  labels=(); for f in "${CORPORA[@]}"; do labels+=("$(corpus_label "$f")"); done
  echo "Corpus scenarios: ${labels[*]}"
  collisions="$(printf '%s\n' "${labels[@]}" | sort | uniq -d | tr '\n' ' ')"
  [[ -z "${collisions// /}" ]] || { echo "::error::corpus scenario labels collide (${collisions% }) — rename the files"; exit 1; }
  for corpus in "${CORPORA[@]}"; do
    out="$(python3 "$here/corpus_parity.py" validate --corpus "$corpus")" || { echo "::error::corpus $(corpus_label "$corpus") failed validation"; exit 1; }
    echo "$out"
    CORPUS_RECORDS["$corpus"]="$(awk '/^corpus OK:/ {print $3}' <<< "$out")"
  done
  rm -rf "$PARITY_STATE"; mkdir -p "$PARITY_STATE"
  if [[ "$CORPUS_BASELINE" == "use" ]]; then
    USING_SAVED_BASELINE=true
    for corpus in "${CORPORA[@]}"; do
      [[ -s "$CORPUS_BASELINE_DIR/$(corpus_label "$corpus").json.gz" ]] || USING_SAVED_BASELINE=false
    done
    [[ "$USING_SAVED_BASELINE" == "true" ]] && echo "Parity baseline: saved responses under $CORPUS_BASELINE_DIR"
  fi
fi

read -ra entries <<< "$CLIENTS"
schedule=()
for (( round = 1; round <= ROUNDS; round++ )); do
  if (( round % 2 )); then schedule+=("${entries[@]}")
  else for (( i = ${#entries[@]} - 1; i >= 0; i-- )); do schedule+=("${entries[i]}"); done; fi
done
echo "Schedule (${ROUNDS} round(s)): ${schedule[*]}"
log_system_provenance

for entry in "${schedule[@]}"; do
  # ctype[@image][#K=V[,K=V]] — the optional env suffix reaches only this arm's node (on top of NODE_ENV_VARS), so
  # one sweep can compare config values of the same image; it is folded into the label so arms stay distinct.
  arm_env=""; spec="$entry"
  if [[ "$spec" == *#* ]]; then arm_env="${spec#*#}"; spec="${spec%%#*}"; fi
  ctype="${spec%%@*}"
  img="$(arm_image "$entry")"
  if [[ -n "$img" ]]; then label="$(arm_label "$ctype" "$img")"
  else img="$NM_IMAGE"; label="$ctype"; fi
  if [[ -n "$arm_env" ]]; then
    label="${label}_$(printf '%s' "$arm_env" | sed -E 's/NETHERMIND_[A-Z]+CONFIG_//g' | tr -c 'a-zA-Z0-9' '_' | tr -s '_' | sed 's/_$//')"
    arm_env="${arm_env//,/ }"
  fi
  LABEL_SEEN["$label"]=$(( ${LABEL_SEEN["$label"]:-0} + 1 ))
  (( LABEL_SEEN["$label"] > 1 )) && label="${label}_r${LABEL_SEEN["$label"]}"
  docker pull "$img" >/dev/null 2>&1 || echo "pull failed — assuming $img is local"
  cst="$STATE_ROOT/$label"; mkdir -p "$cst"
  cname="rpcbench-sweep-${label}-${GITHUB_RUN_ID:-local}"
  echo "::group::sweep ${label} (type=${ctype}, image=${img}, db=${SNAPSHOT_PATH}, head=${SNAPSHOT_BLOCK})"
  if ! CLIENT="$ctype" INSTANCE="primary" NODE_IMAGE="$img" DB_SOURCE="$SNAPSHOT_PATH" DB_ISOLATION="$DB_ISOLATION" \
       SCRATCH_ROOT="$SCRATCH_ROOT" STATE_DIR="$cst" NETWORK="$NETWORK" JSONRPC_MODULES="$JSONRPC_MODULES" \
       LAYOUT_FLAGS="$NM_LAYOUT_FLAGS" ADDITIONAL_FLAGS="" HEALTH_TIMEOUT="$HEALTH_TIMEOUT" DOTTRACE="false" \
       RPC_GAS_CAP="$([[ "$JB_ETH_CALL_CORPUS" == "true" ]] && echo "$CORPUS_RPC_GAS_CAP")" \
       NODE_ENV_VARS="${NODE_ENV_VARS:-}${arm_env:+ $arm_env}" \
       DIAG_DIR="$DIAG_DIR" CONTAINER_NAME="$cname" RPC_PORT="8545" "$here/start-node.sh"; then
    if [[ "$JB_ETH_CALL_CORPUS" == "true" && "$USING_SAVED_BASELINE" != "true" && -z "$BASELINE_LABEL" ]]; then
      echo "::error::${label} failed to start — it was to capture the parity baseline, so later arms are compared against a substitute"; baseline_fail=1
    else
      echo "::warning::${label} failed to start — skipping its cells"
    fi
    echo "::endgroup::"; continue
  fi
  LABELS+=("$label")

  if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
    for corpus in "${CORPORA[@]}"; do run_corpus "$(corpus_label "$corpus")" "$label" "$corpus" "$ctype" "$cname"; done
    [[ -n "$BASELINE_LABEL" ]] || BASELINE_LABEL="$label"
  else
    for rps in $RPS_LIST; do
      for icfg in $ISO_CONFIGS; do
        scen="$(basename "$icfg" .yaml)"; cell="$OUT_DIR/iso/${label}/${rps}/${scen}"
        echo "-- ISO ${label} ${scen} @ rps=${rps} --"
        run_cell "$icfg" "$rps" "$ISO_DURATION" "$cell" "$ctype" "$label" || { echo "::warning::iso ${label}/${scen}/${rps} failed"; cell_fail=$((cell_fail + 1)); }
        [[ -f "$cell/jsonbench-summary.md" ]] && SUMMARIES+=("iso|${scen}|${label}|${rps}=$cell/jsonbench-summary.md")
      done
      mcell="$OUT_DIR/mix/${label}/${rps}"
      echo "-- MIX ${label} @ rps=${rps} --"
      run_cell "$JB_BENCHMARK_CONFIG" "$rps" "$JB_DURATION" "$mcell" "$ctype" "$label" || { echo "::warning::mix ${label}/${rps} failed"; cell_fail=$((cell_fail + 1)); }
      [[ -f "$mcell/jsonbench-summary.md" ]] && SUMMARIES+=("mix|${label}|${rps}=$mcell/jsonbench-summary.md")
    done
  fi

  STATE_DIR="$cst" CONTAINER_NAME="$cname" OUT_DIR="$OUT_DIR" LOG_OUT="$cst/node.log" "$here/stop-node.sh" \
    || { echo "::error::${label}: stop-node failed (DB integrity check or teardown)"; stop_fail=1; }
  scan_node_log "$label" "$ctype" "$cst/node.log"
  echo "::endgroup::"
done

sink="${GITHUB_STEP_SUMMARY:-/dev/stdout}"
{
  echo "# Cross-client sweep — same head ${SNAPSHOT_BLOCK}"
  echo "_${#SUMMARIES[@]} cells · ${ROUNDS} round(s) · seed ${JB_SEED} · isolated dur ${ISO_DURATION}, mixed dur ${JB_DURATION} · json-bench ${JB_REF:-default pin}_"
  [[ "$cell_fail" -gt 0 ]] && { echo; echo "> **⚠️ ${cell_fail} load-test cell(s) failed** — the matrix below is incomplete; the job will fail."; }
  echo
} >> "$sink"
if [[ "${#SUMMARIES[@]}" -gt 0 ]]; then
  printf '%s\n' "${SUMMARIES[@]}" > "$OUT_DIR/summaries.manifest"
  python3 "$here/percat-matrix.py" "@$OUT_DIR/summaries.manifest" >> "$sink" || echo "aggregation failed" >> "$sink"
elif [[ -z "${RPS_LIST// /}" ]]; then
  echo "No k6 cells requested (empty rps_list) — parity/timings only." >> "$sink"
else
  echo "No cell summaries produced — every client failed to start." >> "$sink"; exit 1
fi

if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
  rm -rf "$PARITY_STATE"
  {
    echo
    baseline_desc="${BASELINE_LABEL:-<none started>}"
    [[ "$USING_SAVED_BASELINE" == "true" ]] && baseline_desc="saved ${PARITY_BASE_LABEL[${CORPORA[0]:+$(corpus_label "${CORPORA[0]}")}]:-master}"
    echo "## Corpus parity (baseline = ${baseline_desc})"
    echo
    echo "| corpus | client | matched (+both-error)/total | nonzero defect counters |"
    echo "|---|---|---|---|"
    for row in ${PARITY_ROWS[@]+"${PARITY_ROWS[@]}"}; do
      clabel="${row%%|*}"; rest="${row#*|}"; plabel="${rest%%|*}"; rfile="${rest#*|}"
      jq -r --arg c "$clabel" --arg p "$plabel" \
        '[to_entries[] | select((.value | type == "number") and .value > 0 and (.key != "total") and (.key != "matched") and (.key != "both_rpc_errors")) | "\(.key)=\(.value)"] as $bad
         | "| \($c) | \($p) | \(.matched) (+\(.both_rpc_errors))/\(.total) | \(if ($bad | length) > 0 then ($bad | join(" ")) else "-" end) |"' \
        "$rfile" 2>/dev/null || echo "| $clabel | $plabel | report unreadable | - |"
    done
    [[ "$parity_fail" -gt 0 ]] && { echo; echo "> **⚠️ ${parity_fail} parity failure(s)** — see counters above; the job will fail."; }
    [[ "$parity_skipped" -gt 0 ]] && { echo; echo "> **⚠️ parity not checked for ${parity_skipped} arm/corpus pair(s)** — the comparison against the saved master baseline could not run; the job will fail."; }
  } >> "$sink"
fi

if [[ "${#LABELS[@]}" -ge 2 ]]; then
  { echo; echo "## Cross-client parity (mixed workload responses)"; } >> "$sink"
  for rps in $RPS_LIST; do
    dc=()
    for lbl in "${LABELS[@]}"; do f="$OUT_DIR/mix/${lbl}/${rps}/deep-check-${lbl}.jsonl"; [[ -f "$f" ]] && dc+=("${lbl}=$f"); done
    if [[ "${#dc[@]}" -ge 2 ]]; then
      v="$(python3 "$here/deep-check-compare.py" "${dc[@]}" 2>&1 | grep -iE "requests compared|DIVERGENT|MALFORMED" | tr '\n' ' ' | tr -s ' ')"
      echo "- rps ${rps}: ${v:-<no parity output>}" >> "$sink"
    fi
  done
fi

fail=0
[[ "$node_issue" -eq 0 ]] || { echo "::error::node health issue (Exception / invalid block / missing shutdown marker) in a sweep node log"; fail=1; }
[[ "$cell_fail" -eq 0 ]] || { echo "::error::${cell_fail} load-test cell(s) failed — the matrix is incomplete"; fail=1; }
[[ "$stop_fail" -eq 0 ]] || { echo "::error::stop-node reported a DB-integrity/teardown failure"; fail=1; }
[[ "$parity_fail" -eq 0 ]] || { echo "::error::${parity_fail} corpus parity failure(s) — responses diverged from the baseline or a replay failed"; fail=1; }
# Only reachable with a saved baseline (CORPUS_BASELINE=use), where parity is the run's only correctness gate.
[[ "$parity_skipped" -eq 0 ]] || { echo "::error::${parity_skipped} arm/corpus pair(s) went unchecked against the saved master baseline — the correctness gate did not run"; fail=1; }
[[ "$baseline_fail" -eq 0 ]] || { echo "::error::the configured parity baseline failed to start — parity was measured against a substitute arm"; fail=1; }
exit "$fail"
