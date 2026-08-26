#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Cross-client sweep, one node per client pinned to the same SNAPSHOT_BLOCK: ISOLATED
# (each scenario alone) and MIXED (all together). A node that fails to start has its cells skipped,
# but the incomplete matrix still fails the sweep.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$here/lib.sh"

: "${OUT_DIR:?base output directory}"
: "${STATE_ROOT:?base per-client node state directory}"
: "${SCRATCH_ROOT:?writable scratch root on the snapshot disk}"
: "${NM_IMAGE:?built nethermind image ref}"
: "${SNAPSHOT_BLOCK:?shared head all clients are pinned to}"
: "${JB_REF:?json-bench ref}"
: "${JB_BENCHMARK_CONFIG:?mixed (all-scenario) benchmark config, repo-relative}"

CLIENTS="${CLIENTS:-nethermind}"
# `-` not `:-`: an explicitly empty RPS_LIST means "no k6 cells" (parity/timings only), and
# `:-` would substitute the default over it, which is exactly the case the caller wants.
RPS_LIST="${RPS_LIST-100 250 500}"
ISO_CONFIGS="${ISO_CONFIGS:-}"          # space-separated repo-relative single-scenario configs; empty = mixed only
STATE_LAYOUT="${STATE_LAYOUT:-flat}"
JB_DURATION="${JB_DURATION:-60s}"       # mixed-run load duration
ISO_DURATION="${ISO_DURATION:-20s}"     # per-scenario isolated load duration (shorter; single call)
NETWORK="${NETWORK:-mainnet}"
JSONRPC_MODULES="${JSONRPC_MODULES:-Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-1800}"
DIAG_DIR="${DIAG_DIR:-$SCRATCH_ROOT/diag}"
# Private eth_call corpus mode: every *.jsonl.gz corpus on the runner becomes its own scenario
# (latency cells per rps + a full-corpus parity replay per client, first client = baseline).
# Corpus contents stay on this machine: cells suppress raw tool output and publish aggregate-only
# summaries; parity reports carry counts, never request/response bytes; node logs are scanned as
# counts only and deleted.
JB_ETH_CALL_CORPUS="${JB_ETH_CALL_CORPUS:-false}"
CORPUS_DIR="${CORPUS_DIR:-/data/expb-data/rpc-bench}"   # the workflow passes the selected runner's dir
# Filename filter within CORPUS_DIR — set to an exact filename to run a single corpus.
CORPUS_GLOB="${CORPUS_GLOB:-eth-call-corpus*.jsonl.gz}"
# Size a corpus cell by request count instead of wall time. CORPUS_REQUESTS is absolute;
# CORPUS_PASSES is a multiple of the corpus's own record count (2 = every record drawn twice on
# average). Either one derives the cell duration from the rate, so the rate stays what was asked
# for and only the run length changes. Empty = size cells by JB_DURATION as before.
CORPUS_REQUESTS="${CORPUS_REQUESTS:-}"
CORPUS_PASSES="${CORPUS_PASSES:-}"
# Per-record latency matrix (corpus_parity.py timings): replays the corpus in order so each row is
# attributable to one record, which the k6 cells cannot do. Bypasses k6 entirely, so it is the only
# way to drive a large corpus at a high rate without materializing a request-sized CSV.
CORPUS_TIMINGS_PASSES="${CORPUS_TIMINGS_PASSES:-}"
CORPUS_TIMINGS_RPS="${CORPUS_TIMINGS_RPS:-0}"
CORPUS_TIMINGS_CONCURRENCY="${CORPUS_TIMINGS_CONCURRENCY:-16}"
# Characterise each parity divergence word by word. Derived from response bytes, so opt-in.
CORPUS_PARITY_DIFFS="${CORPUS_PARITY_DIFFS:-false}"
# Sample the node container's cgroup during each corpus cell. Counters only, and a missing cgroup
# is a no-op, so this is on by default: without it a cross-client latency gap cannot be attributed
# to doing more work, waiting on IO, or leaving the machine idle.
CORPUS_RESOURCE_SAMPLING="${CORPUS_RESOURCE_SAMPLING:-true}"
# Discarded load applied to each node before its measured cells. Default covers two 120s cells,
# which is what the 2026-08-13 measurements showed is needed to reach a 0% failure rate; set to 0
# to measure a cold node deliberately.
CORPUS_WARMUP_DURATION="${CORPUS_WARMUP_DURATION:-240s}"
PARITY_STATE="$SCRATCH_ROOT/parity"

# Free-form knobs reach shell arithmetic, where under `set -uo pipefail` (no -e) a value such as
# "250k" silently yields an empty duration and the cell quietly falls back to the workload default.
# Reject anything non-numeric up front, before a node is started.
require_positive_int() {
  local name="$1" value="$2"
  [[ -z "$value" ]] && return 0
  if [[ ! "$value" =~ ^[1-9][0-9]*$ ]]; then
    echo "::error::${name} must be a positive integer, got '${value}'"; exit 1
  fi
}
require_positive_int CORPUS_REQUESTS "$CORPUS_REQUESTS"
require_positive_int CORPUS_PASSES "$CORPUS_PASSES"
require_positive_int CORPUS_TIMINGS_PASSES "$CORPUS_TIMINGS_PASSES"
require_positive_int CORPUS_TIMINGS_CONCURRENCY "$CORPUS_TIMINGS_CONCURRENCY"
if [[ -n "$CORPUS_REQUESTS" && -n "$CORPUS_PASSES" ]]; then
  echo "::error::corpus_requests and corpus_passes are mutually exclusive"; exit 1
fi
# rps entries feed the same arithmetic; an empty list is legal (no k6 cells).
for _rps in $RPS_LIST; do require_positive_int "rps_list entry" "$_rps"; done
# timings_rps may be 0 (unpaced), so it is checked as a non-negative integer instead.
if [[ -n "$CORPUS_TIMINGS_RPS" && ! "$CORPUS_TIMINGS_RPS" =~ ^(0|[1-9][0-9]*)$ ]]; then
  echo "::error::timings_rps must be a non-negative integer, got '${CORPUS_TIMINGS_RPS}'"; exit 1
fi
# The warm-up duration is recorded in timings.meta.json as numeric seconds, so a duration that
# cannot be stated as integer seconds (e.g. "4m") is rejected up front instead of silently
# recording 0 ("measured cold") for a warm run. "0" or "0s" disables the warm-up.
if [[ ! "$CORPUS_WARMUP_DURATION" =~ ^[0-9]+s?$ ]]; then
  echo "::error::corpus_warmup_duration must be integer seconds (optional 's' suffix), got '${CORPUS_WARMUP_DURATION}'"; exit 1
fi
WARMUP_SECONDS="${CORPUS_WARMUP_DURATION%s}"

# Snapshot root differs per benchmark box (/mnt/sda on amd64, /data/nethermind on arm64); the workflow
# passes the resolved one. Each client type has its own block-tagged set there, mirroring the
# single-node path's `<root>/<client>-<block>`; `ctype@image` variants share their type's set, so
# within one client type the image is the only variable.
SNAPSHOT_ROOT="${SNAPSHOT_ROOT:-/data/nethermind}"
NM_LAYOUT_FLAGS="--FlatDb.Enabled=true"

snap_path() {
  case "$1" in
    nethermind) printf '%s' "${SNAPSHOT_ROOT}/nethermind-flat-${SNAPSHOT_BLOCK}" ;;
    *)          printf '%s' "${SNAPSHOT_ROOT}/$1-${SNAPSHOT_BLOCK}" ;;
  esac
}

# Each entry is whitespace-delimited. A `+` segment contains semicolon-delimited flags; commas
# remain part of a flag value (for example, `--JsonRpc.EnabledModules=Eth,Debug`). Validate every
# entry before any node starts so a malformed arm cannot leave a one-arm matrix looking successful.
if [[ "$CLIENTS" == *$'\n'* || "$CLIENTS" == *$'\r'* ]]; then
  echo "::error::clients must be a single line of whitespace-delimited entries"; exit 1
fi
read -r -a SWEEP_ENTRIES <<< "$CLIENTS"
if [[ "${#SWEEP_ENTRIES[@]}" -eq 0 ]]; then
  echo "::error::clients must contain at least one entry"; exit 1
fi
declare -a SWEEP_SPECS=()
declare -a SWEEP_FLAG_SPECS=()
declare -A SWEEP_CTYPES=()
for entry in "${SWEEP_ENTRIES[@]}"; do
  spec="${entry%%+*}"
  arm_flag_spec=""
  [[ "$entry" == *+* ]] && arm_flag_spec="${entry#*+}"
  ctype="${spec%%@*}"
  if [[ -z "$spec" || -z "$ctype" ]]; then
    echo "::error::malformed sweep client entry '${entry}'"; exit 1
  fi
  if [[ "$spec" == *@* && -z "${spec#*@}" ]]; then
    echo "::error::sweep client entry '${entry}' has an empty image"; exit 1
  fi
  if [[ "$arm_flag_spec" == *';'* ]]; then
    if [[ "$arm_flag_spec" == ';'* || "$arm_flag_spec" == *';' || "$arm_flag_spec" == *';;'* ]]; then
      echo "::error::malformed flag list in '${entry}' — use one non-empty flag per ';'"; exit 1
    fi
  fi
  if [[ -n "$arm_flag_spec" ]]; then
    IFS=';' read -r -a parsed_arm_flags <<< "$arm_flag_spec"
    for arm_flag in "${parsed_arm_flags[@]}"; do
      if [[ ! "$arm_flag" =~ ^--[^[:space:]\;]+$ ]]; then
        echo "::error::malformed arm flag '${arm_flag}' in '${entry}' — flags must start with '--' and contain no whitespace or ';'"; exit 1
      fi
    done
  elif [[ "$entry" == *+* ]]; then
    echo "::error::empty arm flag list in '${entry}'"; exit 1
  fi
  SWEEP_SPECS+=("$spec")
  SWEEP_FLAG_SPECS+=("$arm_flag_spec")
  SWEEP_CTYPES["$ctype"]=1
done
# A client whose snapshot set is absent reaches start-node.sh with a DB_SOURCE that does not exist,
# and that is only a per-client `::warning::` — the sweep would report a two-thirds-empty matrix as
# success, and in corpus mode a baseline with no candidate, i.e. no parity verdict at all. Check
# every requested type up front instead, and only the state_layout axis the request actually uses
# (a reth-only sweep resolves no Nethermind path, so the flat pin does not apply to it).
SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")" || { echo "::error::cannot canonicalize SCRATCH_ROOT"; exit 1; }
assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
for ctype in "${!SWEEP_CTYPES[@]}"; do
  case "$ctype" in
    nethermind|geth|reth) ;;
    *) echo "::error::unknown sweep client type '${ctype}' (expected nethermind | geth | reth)"; exit 1 ;;
  esac
  if [[ ! -d "$(snap_path "$ctype")" ]]; then
    echo "::error::no ${ctype} snapshot set at $(snap_path "$ctype") — this box cannot serve that client"
    echo "::error::snapshot sets present under ${SNAPSHOT_ROOT}:"
    ls -1d "${SNAPSHOT_ROOT}"/*-"${SNAPSHOT_BLOCK}" 2>/dev/null | sed 's|^|::error::  |' || true
    exit 1
  fi
  snap_real="$(realpath -e -- "$(snap_path "$ctype")")" || {
    echo "::error::cannot canonicalize ${ctype} snapshot at $(snap_path "$ctype")"; exit 1
  }
  [[ "$snap_real" != "$SCRATCH_ROOT" ]] || { echo "::error::SCRATCH_ROOT must not equal the ${ctype} snapshot"; exit 1; }
  case "$snap_real/" in
    "$SCRATCH_ROOT"/*) echo "::error::SCRATCH_ROOT must not contain the ${ctype} snapshot"; exit 1 ;;
  esac
  case "$SCRATCH_ROOT/" in
    "$snap_real"/*) echo "::error::SCRATCH_ROOT must not be inside the ${ctype} snapshot"; exit 1 ;;
  esac
done
if [[ ! -d "$SCRATCH_ROOT" ]] && ! as_root mkdir -p -- "$SCRATCH_ROOT"; then
  echo "::error::failed to create SCRATCH_ROOT '$SCRATCH_ROOT'"; exit 1
fi
[[ -d "$SCRATCH_ROOT" ]] || { echo "::error::SCRATCH_ROOT '$SCRATCH_ROOT' is not a directory"; exit 1; }
SCRATCH_ROOT="$(realpath -e -- "$SCRATCH_ROOT")" || { echo "::error::cannot canonicalize SCRATCH_ROOT"; exit 1; }
if [[ -n "${SWEEP_CTYPES[nethermind]:-}" && "$STATE_LAYOUT" != "flat" ]]; then
  echo "::error::sweep mode resolves a flat Nethermind snapshot, so state_layout '${STATE_LAYOUT}' cannot run here"; exit 1
fi
# One isolation mode per client type, so storage counters stay comparable between the images of one
# type: overlayfs adds a layer and changes readahead and page-cache behaviour, so
# disk-read-per-request would otherwise measure a harness difference as much as a code one. Across
# types they can differ (reth cannot use overlay at all, below) — never read a cross-type disk-read
# delta as a code difference. DB_ISOLATION_ALL forces one mode on every node; `copy` is the
# overlay-free choice that still leaves the snapshot intact.
#
# `direct` is refused without explicit consent. It bind-mounts the snapshot READ-WRITE, and a node's
# startup alone rewrites RocksDB MANIFEST/CURRENT/WAL and triggers flushes across every column
# family. The Nethermind snapshots on these boxes are shared with expb, so one direct run
# silently replaces the fixture every later benchmark compares against — which happened on
# 2026-08-13 and cost a day of measurements (eth_call p99 tripled while an untouched client moved 2%).
DB_ISOLATION_ALL="${DB_ISOLATION_ALL:-}"
DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION="${DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION:-false}"
if [[ "$DB_ISOLATION_ALL" == "direct" && "$DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION" != "true" ]]; then
  echo "::error::DB_ISOLATION_ALL=direct mutates the shared snapshot. Use 'copy' for an overlay-free"
  echo "::error::comparison, or set DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION=true on a private snapshot."
  exit 1
fi
# reth's DB is a single large mdbx.dat, and its startup write forces overlayfs to copy the whole file
# up before the node opens (~200 s on the mainnet set, and the copy has to fit on the box), so reth
# runs `direct` — a read-write bind mount of its own set. That set is reth-only; the refusal above
# exists because the *Nethermind* sets are shared with expb.
db_isolation_for() {
  if [[ -n "$DB_ISOLATION_ALL" ]]; then printf '%s' "$DB_ISOLATION_ALL"; return; fi
  case "$1" in
    reth) printf 'direct' ;;
    *)    printf 'overlay' ;;
  esac
}

# One json-bench cell: $1=config (repo-relative) $2=rps $3=duration $4=out dir $5=client
# $6=label $7=corpus file (empty = normal cell; set = private corpus cell, aggregate-only output)
run_cell() {
  local cfg="$1" rps="$2" dur="$3" cell="$4" ctype="$5" label="$6" corpus="${7:-}" node="${8:-}"
  local is_corpus="false" deep="true"
  [[ -n "$corpus" ]] && { is_corpus="true"; deep="false"; }
  mkdir -p "$cell"
  # run-jsonbench.sh owns the sampling window so it covers container execution only, and it
  # normalizes against the request count k6 reports rather than one derived from the duration.
  local sampler_container="" sampler_out=""
  if [[ -n "$node" && "$CORPUS_RESOURCE_SAMPLING" == "true" ]]; then
    sampler_container="$node"; sampler_out="$cell/resources.json"
  fi
  OUT_DIR="$cell" RPC_URL="http://localhost:8545" CLIENT_TYPE="$ctype" LABEL="$label" \
    SCRATCH_ROOT="$SCRATCH_ROOT" JB_REF="$JB_REF" JB_MODE="benchmark" \
    JB_BENCHMARK_CONFIG="$cfg" JB_RPS="$rps" JB_DURATION="$dur" \
    JB_DEEP_CHECK="$deep" JB_HTML_REPORT="false" \
    JB_ETH_CALL_CORPUS="$is_corpus" JB_ETH_CALL_CORPUS_FILE="$corpus" \
    RESOURCE_SAMPLER_CONTAINER="$sampler_container" RESOURCE_SAMPLER_OUT="$sampler_out" \
    "$here/run-jsonbench.sh"
}

# Print a cell's failure rate next to its name. Latency percentiles above the failure rate are
# measuring failures, not latency: at a 2% failure rate p99 sits inside the failing population and
# reads as a huge regression when nothing got slower. Surfacing it per cell keeps that visible.
report_fail_rate() {
  local cell="$1" name="$2" rate
  [[ -s "$cell/summary.json" ]] || return 0
  rate="$(python3 - "$cell/summary.json" <<'PY' 2>/dev/null || echo ""
import json, sys
try:
    m = (json.load(open(sys.argv[1])) or {}).get("metrics", {}) or {}
    r = ((m.get("http_req_failed") or {}).get("values") or {}).get("rate")
    print("%.3f" % (r * 100) if isinstance(r, (int, float)) else "")
except Exception:
    print("")
PY
)"
  [[ -n "$rate" ]] || return 0
  if awk "BEGIN{exit !($rate > ${JB_MAX_FAIL_RATE_PCT:-1})}" 2>/dev/null; then
    echo "::warning::${name}: ${rate}% of requests failed — percentiles at or above p$(awk "BEGIN{printf \"%d\", 100-$rate}") describe failures, not latency"
  else
    echo "   ${name}: fail rate ${rate}%"
  fi
}

# Corpus mode raises start-node's uniform RPC_GAS_CAP (default 1e9) to 1e12: captured calls
# carry explicit gas up to billions, and clamping them would make calls fail artificially.
CORPUS_RPC_GAS_CAP="1000000000000"

# Cell length for a corpus at a given rate. With CORPUS_REQUESTS/CORPUS_PASSES the cell is sized by
# request count instead of wall time: k6's constant-arrival-rate executor holds the rate, so running
# for count/rate seconds delivers exactly that many requests at exactly the rate asked for.
corpus_cell_duration() {
  local corpus="$1" rps="$2" target=""
  if [[ -n "$CORPUS_REQUESTS" ]]; then
    target="$CORPUS_REQUESTS"
  elif [[ -n "$CORPUS_PASSES" ]]; then
    local records="${CORPUS_RECORDS[$corpus]:-}"
    if [[ -z "$records" ]]; then
      echo "::warning::no record count for $(corpus_label "$corpus") — falling back to JB_DURATION" >&2
      printf '%s' "$JB_DURATION"; return
    fi
    target=$((records * CORPUS_PASSES))
  else
    printf '%s' "$JB_DURATION"; return
  fi
  # Round up so the cell never delivers fewer than the requested count.
  printf '%ss' "$(( (target + rps - 1) / rps ))"
}

# Short scenario label from a corpus filename: eth-call-corpus[-<label>].jsonl.gz -> <label> | default
corpus_label() {
  local b; b="$(basename "$1")"
  b="${b#eth-call-corpus}"; b="${b#-}"; b="${b%.jsonl.gz}"
  printf '%s' "${b:-default}" | tr -c 'a-zA-Z0-9._\n' '-'
}

mkdir -p "$OUT_DIR" "$STATE_ROOT"
declare -a SUMMARIES=()
declare -a LABELS=()
declare -a CORPORA=()
declare -a PARITY_ROWS=()
node_issue=0
start_fail=0  # requested arm failed to start; an incomplete matrix must fail the sweep
cell_fail=0   # load-test cells that ran but failed (distinct from a client skipped for never starting)
stop_fail=0   # stop-node.sh reported a DB-integrity/teardown failure (overlay clients; direct only warns)
parity_fail=0 # corpus parity defects or a failed parity replay
BASELINE_LABEL=""  # first successfully started client; all later clients diff against it

case "$JB_ETH_CALL_CORPUS" in
  true|false) ;;
  *) echo "::error::JB_ETH_CALL_CORPUS must be true or false"; exit 1 ;;
esac
if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
  for f in "$CORPUS_DIR"/$CORPUS_GLOB; do
    [[ -f "$f" ]] && CORPORA+=("$f")
  done
  if [[ "${#CORPORA[@]}" -eq 0 ]]; then
    echo "::error::no corpus files matching '$CORPUS_GLOB' under $CORPUS_DIR"; exit 1
  fi
  CORPUS_LABELS=()
  for f in "${CORPORA[@]}"; do CORPUS_LABELS+=("$(corpus_label "$f")"); done
  echo "Corpus scenarios: ${CORPUS_LABELS[*]}"
  # corpus_label sanitizes the filename, so two distinct corpora can collapse onto one label and
  # would then share a parity baseline and cell directory — the second baseline overwrites the
  # first and later clients diff against the wrong one. It fails safe (a count mismatch), but far
  # into the sweep and pointing at nothing real, so reject it before any node starts.
  collisions="$(printf '%s\n' "${CORPUS_LABELS[@]}" | sort | uniq -d | tr '\n' ' ')"
  if [[ -n "${collisions// /}" ]]; then
    echo "::error::corpus scenario labels collide (${collisions%% }) — rename the files so each yields a distinct label"; exit 1
  fi
  # Fail on an unreadable/oversized corpus in seconds, before any node starts or cell runs.
  declare -A CORPUS_RECORDS=()
  for corpus in "${CORPORA[@]}"; do
    # validate prints "corpus OK: <n> records" — reuse that count for CORPUS_PASSES sizing.
    if validate_out="$(python3 "$here/corpus_parity.py" validate --corpus "$corpus")"; then
      echo "$validate_out"
      CORPUS_RECORDS["$corpus"]="$(printf '%s' "$validate_out" | awk '/^corpus OK:/ {print $3}')"
    else
      echo "::error::corpus $(corpus_label "$corpus") failed validation — fix the file before sweeping"; exit 1
    fi
  done
  rm -rf "$PARITY_STATE"; mkdir -p "$PARITY_STATE"
fi

# Each entry is `ctype[@image][+flag;flag;...]` (e.g. nethermind@nethermindeth/nethermind:master) for
# same-client version comparisons. Sequential (one node up at a time), so same-snapshot variants are safe.
# Listing the same image twice is how a sweep measures its own run-to-run drift, so repeats get a
# distinct label: sharing one would make each repeat overwrite the previous one's cells and state,
# and the sweep would silently report fewer results than it ran.
#
# The `+` segment appends node flags for that arm alone, which is what makes a flag itself A/B-able:
# the same image can appear twice with different flags, and the flags are part of the label so the
# two arms neither collide nor need reading from the config to tell apart. `{ARM_SCRATCH}` in a flag
# expands to an empty per-arm directory (wiped here, not inherited from an earlier arm or dispatch) —
# a flag pointing a node's storage there measures every arm against the same starting state instead
# of the state its predecessor left behind. Semicolons separate flags; commas are preserved in values.
# Image tags cannot contain `+`, so the split is unambiguous.
log_system_provenance

declare -A LABEL_SEEN=()
for arm_index in "${!SWEEP_ENTRIES[@]}"; do
  entry="${SWEEP_ENTRIES[$arm_index]}"
  spec="${SWEEP_SPECS[$arm_index]}"
  arm_flag_spec="${SWEEP_FLAG_SPECS[$arm_index]}"
  arm_flags=""
  if [[ -n "$arm_flag_spec" ]]; then
    IFS=';' read -r -a parsed_arm_flags <<< "$arm_flag_spec"
    arm_flags="${parsed_arm_flags[*]}"
  fi
  ctype="${spec%%@*}"
  if [[ "$spec" == *@* ]]; then
    img="${spec#*@}"; label="${ctype}_$(printf '%s' "${img##*:}" | tr -c 'a-zA-Z0-9' '_')"
  else
    img="$NM_IMAGE"; label="$ctype"
  fi
  arm_scratch_dir=""
  if [[ -n "$arm_flag_spec" ]]; then
    # Keep a readable prefix, but derive uniqueness from the complete unexpanded specification.
    # A prefix alone made flags sharing their first 24 characters collide and become _rN repeats.
    arm_flag_hash="$(printf '%s' "$arm_flag_spec" | sha256sum | cut -c1-12)"
    arm_flag_prefix="$(printf '%s' "$arm_flag_spec" | tr -c 'a-zA-Z0-9' '_' | cut -c1-24)"
    label="${label}_${arm_flag_prefix}_${arm_flag_hash}"
  fi
  LABEL_SEEN["$label"]=$(( ${LABEL_SEEN["$label"]:-0} + 1 ))
  (( ${LABEL_SEEN["$label"]} > 1 )) && label="${label}_r${LABEL_SEEN["$label"]}"
  if [[ "$arm_flag_spec" == *"{ARM_SCRATCH}"* ]]; then
    arm_root="$SCRATCH_ROOT/arm"
    if [[ -L "$arm_root" ]]; then
      echo "::error::per-arm scratch root '$arm_root' must not be a symlink"; exit 1
    fi
    if [[ ! -d "$arm_root" ]] && ! as_root mkdir -p -- "$arm_root"; then
      echo "::error::failed to create per-arm scratch root '$arm_root'"; exit 1
    fi
    [[ -d "$arm_root" && ! -L "$arm_root" ]] || {
      echo "::error::per-arm scratch root '$arm_root' is not a directory"; exit 1
    }
    arm_root="$(realpath -e -- "$arm_root")" || {
      echo "::error::cannot canonicalize per-arm scratch root"; exit 1
    }
    [[ "$arm_root/" == "$SCRATCH_ROOT/"* ]] || {
      echo "::error::per-arm scratch root must be inside SCRATCH_ROOT"; exit 1
    }
    arm_scratch_dir="$arm_root/$label"
    assert_sane_dir "$arm_scratch_dir" "ARM_SCRATCH_DIR"
    assert_no_mounts_under "$arm_scratch_dir"
    if ! as_root rm -rf -- "$arm_scratch_dir"; then
      echo "::error::failed to wipe per-arm scratch directory '$arm_scratch_dir'"; exit 1
    fi
    if ! as_root mkdir -p -- "$arm_scratch_dir"; then
      echo "::error::failed to recreate per-arm scratch directory '$arm_scratch_dir'"; exit 1
    fi
    if [[ ! -d "$arm_scratch_dir" || -L "$arm_scratch_dir" ]]; then
      echo "::error::per-arm scratch path '$arm_scratch_dir' is not a directory after recreation"; exit 1
    fi
    arm_flags="${arm_flags//\{ARM_SCRATCH\}/$arm_scratch_dir}"
  fi
  [[ -n "$arm_flags" ]] && echo "arm flags: $arm_flags"
  docker pull "$img" >/dev/null 2>&1 || echo "pull failed — assuming $img is local"
  cst="$STATE_ROOT/$label"; mkdir -p "$cst"
  cname="rpcbench-sweep-${label}-${GITHUB_RUN_ID:-local}"
  snap="$(snap_path "$ctype")"; iso="$(db_isolation_for "$ctype")"
  echo "::group::sweep ${label} (type=${ctype}, image=${img}, db=${snap}, isolation=${iso}, head=${SNAPSHOT_BLOCK})"
  if ! CLIENT="$ctype" INSTANCE="primary" NODE_IMAGE="$img" \
       DB_SOURCE="$snap" DB_ISOLATION="$iso" \
       SCRATCH_ROOT="$SCRATCH_ROOT" STATE_DIR="$cst" NETWORK="$NETWORK" \
       JSONRPC_MODULES="$JSONRPC_MODULES" LAYOUT_FLAGS="$NM_LAYOUT_FLAGS" \
       ADDITIONAL_FLAGS="$arm_flags" ARM_SCRATCH_DIR="$arm_scratch_dir" \
       HEALTH_TIMEOUT="$HEALTH_TIMEOUT" DOTTRACE="false" \
       RPC_GAS_CAP="$([[ "$JB_ETH_CALL_CORPUS" == "true" ]] && echo "$CORPUS_RPC_GAS_CAP")" \
       DIAG_DIR="$DIAG_DIR" CONTAINER_NAME="$cname" RPC_PORT="8545" \
       "$here/start-node.sh"; then
    echo "::warning::${label} failed to start — skipping its cells"; start_fail=$((start_fail + 1)); echo "::endgroup::"; continue
  fi
  LABELS+=("$label")

  if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
    # One latency cell per corpus per rps, then one full-corpus parity replay per corpus
    # while the node is still up. The first started client is the parity baseline.
    for corpus in "${CORPORA[@]}"; do
      clabel="$(corpus_label "$corpus")"

      # A node that has just started answers eth_blockNumber (what wait_for_rpc gates on) long
      # before it serves eth_call from state. Measured cold, the first cell fails ~2% of requests
      # and reports roughly 60% higher p99 than the same node warm — an effect larger than any
      # code change this harness is used to detect. Burn that off into a discarded cell first.
      # 2026-08-13 evidence: with 120s/cell, cell 1 failed 1.97%, cell 2 <1%, cell 3 0.000%.
      # Parity/timings-only runs (empty rps_list) warm too — they measure the same node, and a
      # cold matrix looks exactly as authoritative as a warm one. Runs once per corpus on
      # purpose: different corpora can touch disjoint state.
      WARMED_SECONDS=0
      if (( WARMUP_SECONDS > 0 )); then
        # Warm at the highest rate the run will measure — the k6 cells AND the timings matrix
        # both count, so the max spans both knobs; unpaced timings (0) falls back flat.
        warm_rps=0
        for r in $RPS_LIST; do (( r > warm_rps )) && warm_rps=$r; done
        [[ -n "$CORPUS_TIMINGS_PASSES" ]] && (( CORPUS_TIMINGS_RPS > warm_rps )) && warm_rps="$CORPUS_TIMINGS_RPS"
        (( warm_rps == 0 )) && warm_rps=100
        # Discarded output must stay OUT of OUT_DIR: stage() publishes by filename and comment()
        # keys cells by directory position, so a staged warmup summary.json would displace the
        # measured cell in the published PR comment.
        warm_cell="$SCRATCH_ROOT/warmup-cell/${clabel}/${label}"
        echo "-- WARMUP ${clabel} ${label} @ rps=${warm_rps} for ${WARMUP_SECONDS}s (discarded) --"
        # warmup_seconds must state the OUTCOME. On the k6 branch the request IS the outcome:
        # the constant-arrival executor runs for exactly the given duration, and an elapsed
        # clock here would bill the whole json-bench wrapper (clone, docker build, fixture
        # write — which scales with corpus size) as warm-up the idle node never received. The
        # replay branch is request-count-bounded (pacing can only delay: its ceiling is
        # concurrency/latency), so THERE an elapsed clock plus a hard `timeout` state the
        # truth; hitting the bound still IS a completed warm-up — the node absorbed load for
        # the whole window.
        if [[ -n "$RPS_LIST" ]]; then
          # The k6 cells build the JSON-array fixture anyway, so warming through run_cell adds no
          # extra materialization. The fail-rate gate is LIFTED for this cell: the warm-up exists
          # to absorb exactly the cold failures the gate rejects, so gating it inverts its exit
          # status (a warm-up that did its job would report failure).
          if JB_MAX_FAIL_RATE_PCT=100 run_cell "$JB_BENCHMARK_CONFIG" "$warm_rps" "${WARMUP_SECONDS}s" \
              "$warm_cell" "$ctype" "$label" "$corpus" ""; then
            WARMED_SECONDS="$WARMUP_SECONDS"
          else
            echo "::warning::warmup for ${label} failed — measured cells may be cold (recorded warmup_seconds=0)"
          fi
          report_fail_rate "$warm_cell" "warmup ${clabel}/${label}"
        elif [[ -z "${CORPUS_RECORDS[$corpus]:-}" ]]; then
          # Cannot size the replay without the record count (validate fills it for every corpus,
          # so this is defensive). A wrong guess would multiply by the real count inside
          # timings() and run for hours; skipping states the truth: not warmed.
          echo "::warning::no record count for $(corpus_label "$corpus") — skipping warmup (recorded warmup_seconds=0)"
        else
          # Fixture-free mode: an empty rps_list exists so a large corpus never materializes the
          # k6 JSON-array fixture, so the warm-up must not build it either. corpus_parity's
          # paced replay drives the same eth_calls without k6; it exits nonzero only on a real
          # crash (cold per-call failures are reported in its output, not the exit code).
          records="${CORPUS_RECORDS[$corpus]}"
          warm_passes=$(( (warm_rps * WARMUP_SECONDS + records - 1) / records ))
          mkdir -p "$warm_cell"
          warm_started=$SECONDS
          timeout $(( WARMUP_SECONDS + 60 )) python3 "$here/corpus_parity.py" timings \
              --corpus "$corpus" --rpc-url "http://localhost:8545" \
              --out "$warm_cell/warmup-timings.csv" --passes "$warm_passes" \
              --rps "$warm_rps" --concurrency "$CORPUS_TIMINGS_CONCURRENCY"
          warm_status=$?
          # 124 = the timeout fired: the node still absorbed warm load for the whole window.
          if [[ "$warm_status" -eq 0 || "$warm_status" -eq 124 ]]; then
            WARMED_SECONDS=$(( SECONDS - warm_started ))
          else
            echo "::warning::warmup replay for ${label} failed — measured cells may be cold (recorded warmup_seconds=0)"
          fi
        fi
      fi

      # An empty rps_list runs no k6 cells: for a large corpus the JSON-array fixture alone can
      # exceed the box, and parity/timings do not need it.
      # A repeated rate is a deliberate drift control, so each repeat needs its own cell directory —
      # sharing one silently leaves only the last result behind.
      declare -A RPS_SEEN=()
      for rps in $RPS_LIST; do
        RPS_SEEN["$rps"]=$(( ${RPS_SEEN["$rps"]:-0} + 1 ))
        slot="$rps"
        (( ${RPS_SEEN["$rps"]} > 1 )) && slot="${rps}_r${RPS_SEEN["$rps"]}"
        cell="$OUT_DIR/corpus/${clabel}/${label}/${slot}"
        cell_duration="$(corpus_cell_duration "$corpus" "$rps")"
        echo "-- CORPUS ${clabel} ${label} @ rps=${rps} for ${cell_duration} --"
        run_cell "$JB_BENCHMARK_CONFIG" "$rps" "$cell_duration" "$cell" "$ctype" "$label" "$corpus" "$cname" \
          || { echo "::warning::corpus ${clabel}/${label}/${slot} failed"; cell_fail=$((cell_fail + 1)); }
        report_fail_rate "$cell" "${clabel}/${label}/${slot}"
        [[ -f "$cell/jsonbench-summary.md" ]] && SUMMARIES+=("iso|${clabel}|${label}|${slot}=$cell/jsonbench-summary.md")
      done
      unset RPS_SEEN
      if [[ -z "$BASELINE_LABEL" ]]; then
        echo "-- PARITY ${clabel}: capturing baseline (${label}) --"
        if ! python3 "$here/corpus_parity.py" baseline \
            --corpus "$corpus" --rpc-url "http://localhost:8545" \
            --state "$PARITY_STATE/${clabel}.json"; then
          echo "::error::parity baseline capture failed for corpus ${clabel} on ${label}"
          parity_fail=$((parity_fail + 1))
        fi
      else
        report_dir="$OUT_DIR/corpus/${clabel}/${label}"; mkdir -p "$report_dir"
        report="$report_dir/parity.json"
        echo "-- PARITY ${clabel}: ${label} vs baseline ${BASELINE_LABEL} --"
        if python3 "$here/corpus_parity.py" compare \
            --corpus "$corpus" --rpc-url "http://localhost:8545" \
            --state "$PARITY_STATE/${clabel}.json" --report "$report" \
            --baseline-client "$BASELINE_LABEL" --candidate-client "$label" \
            $([[ "$CORPUS_PARITY_DIFFS" == "true" ]] && echo "--diffs $report_dir/parity-diffs.json"); then
          PARITY_ROWS+=("${clabel}|${label}|$report")
        else
          echo "::warning::parity defects for ${label} vs ${BASELINE_LABEL} on corpus ${clabel} (see report counts)"
          parity_fail=$((parity_fail + 1))
          [[ -f "$report" ]] && PARITY_ROWS+=("${clabel}|${label}|$report")
        fi
      fi

      if [[ -n "$CORPUS_TIMINGS_PASSES" ]]; then
        tdir="$OUT_DIR/corpus/${clabel}/${label}"; mkdir -p "$tdir"
        echo "-- TIMINGS ${clabel}: ${label} (${CORPUS_TIMINGS_PASSES} passes @ ${CORPUS_TIMINGS_RPS} rps) --"
        if ! python3 "$here/corpus_parity.py" timings \
            --corpus "$corpus" --rpc-url "http://localhost:8545" \
            --out "$tdir/timings.csv" --passes "$CORPUS_TIMINGS_PASSES" \
            --rps "$CORPUS_TIMINGS_RPS" --concurrency "$CORPUS_TIMINGS_CONCURRENCY" \
            --warmup-seconds "$WARMED_SECONDS"; then
          echo "::warning::timings replay failed for ${label} on corpus ${clabel}"
          cell_fail=$((cell_fail + 1))
        fi
      fi
    done
    [[ -z "$BASELINE_LABEL" ]] && BASELINE_LABEL="$label"
  else
  for rps in $RPS_LIST; do
    # ISOLATED: each scenario alone
    for icfg in $ISO_CONFIGS; do
      scen="$(basename "$icfg" .yaml)"
      cell="$OUT_DIR/iso/${label}/${rps}/${scen}"
      echo "-- ISO ${label} ${scen} @ rps=${rps} --"
      run_cell "$icfg" "$rps" "$ISO_DURATION" "$cell" "$ctype" "$label" || { echo "::warning::iso ${label}/${scen}/${rps} failed"; cell_fail=$((cell_fail + 1)); }
      [[ -f "$cell/jsonbench-summary.md" ]] && SUMMARIES+=("iso|${scen}|${label}|${rps}=$cell/jsonbench-summary.md")
    done
    # MIXED: all scenarios together
    mcell="$OUT_DIR/mix/${label}/${rps}"
    echo "-- MIX ${label} @ rps=${rps} --"
    run_cell "$JB_BENCHMARK_CONFIG" "$rps" "$JB_DURATION" "$mcell" "$ctype" "$label" || { echo "::warning::mix ${label}/${rps} failed"; cell_fail=$((cell_fail + 1)); }
    [[ -f "$mcell/jsonbench-summary.md" ]] && SUMMARIES+=("mix|${label}|${rps}=$mcell/jsonbench-summary.md")
  done
  fi

  # stop-node.sh verifies the snapshot is pristine and exits non-zero on a DB-integrity/teardown failure. That must fail
  # the sweep — not degrade to a warning. reth 'direct' legitimately mutates and stop-node warns-not-fails, so this only
  # trips overlay clients.
  if ! STATE_DIR="$cst" CONTAINER_NAME="$cname" OUT_DIR="$OUT_DIR" LOG_OUT="$cst/node.log" \
       "$here/stop-node.sh"; then
    echo "::error::${label}: stop-node failed (DB integrity check or teardown) — failing the sweep"; stop_fail=1
  fi
  # Sweep mode isn't covered by the workflow's log-scan step, so scan each node log here with the same four checks.
  # Corpus mode prints COUNTS only (log lines could quote private call data) and deletes the log afterwards.
  if [[ -f "$cst/node.log" ]]; then
    clean="$cst/node.clean.log"
    sed -E 's/\x1B\[[0-9;?]*[ -/]*[@-~]//g' "$cst/node.log" > "$clean"
    grep -in "Exception" "$clean" | grep -vF 'Incorrect JSON RPC parameters' > "$cst/node.exc" || true
    exc_count="$(wc -l < "$cst/node.exc" | tr -d ' ')"
    if [[ "$ctype" == "nethermind" ]]; then
      # Exception / invalid-block / shutdown-marker wording is Nethermind-specific — gate only on NM cells.
      if [[ -s "$cst/node.exc" ]]; then
        echo "::warning::${label}: ${exc_count} Exception line(s) in node log"
        [[ "$JB_ETH_CALL_CORPUS" != "true" ]] && head -20 "$cst/node.exc"
        node_issue=1
      fi
      if grep -qEi 'invalid[[:space:]_-]*block' "$clean"; then echo "::warning::${label}: invalid block in node log"; node_issue=1; fi
      # A missing marker means docker SIGKILLed a hung node or shutdown crashed — run untrustworthy.
      if ! grep -q "Nethermind is shut down" "$clean"; then
        echo "::warning::${label}: 'Nethermind is shut down' marker not found — node did not shut down cleanly"; node_issue=1
      fi
    elif [[ -s "$cst/node.exc" ]]; then
      # geth/reth: NM wording false-positives, so warn only — don't gate on the reference clients.
      echo "::warning::${label}: ${exc_count} Exception-like line(s) in node log (warn only, non-Nethermind)"
      [[ "$JB_ETH_CALL_CORPUS" != "true" ]] && head -20 "$cst/node.exc"
    fi
    # Severe patterns: warn-only for every client (mirrors the workflow's non-gating scan).
    for pattern in "Unhandled" "Fatal" "ERROR"; do
      if grep -qi "$pattern" "$clean"; then
        echo "::warning::${label}: severe log pattern '$pattern' ($(grep -ci "$pattern" "$clean") line(s))"
        [[ "$JB_ETH_CALL_CORPUS" != "true" ]] && { grep -in "$pattern" "$clean" | head -10 || true; }
      fi
    done
    if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
      rm -f "$cst/node.log" "$clean" "$cst/node.exc"
    fi
  fi
  echo "::endgroup::"
done

sink="${GITHUB_STEP_SUMMARY:-/dev/stdout}"
{
  echo "# Cross-client sweep — same head ${SNAPSHOT_BLOCK}"
  echo "_${#SUMMARIES[@]} cells · isolated dur ${ISO_DURATION}, mixed dur ${JB_DURATION} · json-bench ${JB_REF}_"
  [[ "$cell_fail" -gt 0 ]] && { echo; echo "> **⚠️ ${cell_fail} load-test cell(s) failed** — the matrix below is incomplete; the job will fail."; }
  echo
} >> "$sink"
if [[ "${#SUMMARIES[@]}" -gt 0 ]]; then
  printf '%s\n' "${SUMMARIES[@]}" > "$OUT_DIR/summaries.manifest"  # via file — 100+ cells exceed ARG_MAX
  python3 "$here/percat-matrix.py" "@$OUT_DIR/summaries.manifest" >> "$sink" || echo "aggregation failed" >> "$sink"
elif [[ -z "${RPS_LIST// /}" ]]; then
  # Documented mode: an empty rps_list requests no k6 cells at all (parity/timings only), so
  # having no summaries is the expected outcome, not a failed sweep. Keep going so the parity
  # table still renders and the real failure counters below decide the exit status.
  echo "No k6 cells requested (empty rps_list) — parity/timings only." >> "$sink"
else
  echo "No cell summaries produced — every client failed to start." >> "$sink"; exit 1
fi

# Corpus parity table (counts only — no request/response content).
if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
  rm -rf "$PARITY_STATE"
  {
    echo
    echo "## Corpus parity (baseline = ${BASELINE_LABEL:-<none started>})"
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
  } >> "$sink"
fi

# Cross-client response parity per rps (deep_check diff over the mixed workload — the "compare" half of a
# json-bench comparison, so the sweep gives latency AND correctness in one job).
if [[ "${#LABELS[@]}" -ge 2 ]]; then
  { echo; echo "## Cross-client parity (mixed workload responses)"; } >> "$sink"
  for rps in $RPS_LIST; do
    dc=()
    for lbl in "${LABELS[@]}"; do
      f="$OUT_DIR/mix/${lbl}/${rps}/deep-check-${lbl}.jsonl"
      [[ -f "$f" ]] && dc+=("${lbl}=$f")
    done
    if [[ "${#dc[@]}" -ge 2 ]]; then
      v="$(python3 "$here/deep-check-compare.py" "${dc[@]}" 2>&1 | grep -iE "requests compared|DIVERGENT|MALFORMED" | tr '\n' ' ' | tr -s ' ')"
      echo "- rps ${rps}: ${v:-<no parity output>}" >> "$sink"
    fi
  done
fi
fail=0
if [[ "${#LABELS[@]}" -eq 0 ]]; then
  echo "::error::no sweep arms started successfully — refusing to report an empty matrix as success"; fail=1
fi
if [[ "$start_fail" -gt 0 ]]; then
  echo "::error::${start_fail} sweep arm(s) failed to start — the matrix is incomplete, failing"; fail=1
fi
if [[ "$node_issue" -eq 1 ]]; then
  echo "::error::node health issue (Exception / invalid block / missing shutdown marker) in a sweep node log — failing"; fail=1
fi
if [[ "$cell_fail" -gt 0 ]]; then
  echo "::error::${cell_fail} load-test cell(s) failed — the matrix is incomplete, failing"; fail=1
fi
if [[ "$stop_fail" -eq 1 ]]; then
  echo "::error::stop-node reported a DB-integrity/teardown failure — failing"; fail=1
fi
if [[ "$parity_fail" -gt 0 ]]; then
  echo "::error::${parity_fail} corpus parity failure(s) — responses diverged from the baseline client or a replay failed"; fail=1
fi
exit "$fail"
