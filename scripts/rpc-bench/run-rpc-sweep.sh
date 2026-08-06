#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Cross-client sweep, one node per client pinned to the same SNAPSHOT_BLOCK: ISOLATED
# (each scenario alone) and MIXED (all together). A node that fails to start is skipped.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

: "${OUT_DIR:?base output directory}"
: "${STATE_ROOT:?base per-client node state directory}"
: "${SCRATCH_ROOT:?writable scratch root on the snapshot disk}"
: "${NM_IMAGE:?built nethermind image ref}"
: "${SNAPSHOT_BLOCK:?shared head all clients are pinned to}"
: "${JB_REF:?json-bench ref}"
: "${JB_BENCHMARK_CONFIG:?mixed (all-scenario) benchmark config, repo-relative}"

CLIENTS="${CLIENTS:-nethermind geth reth}"
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
# Profiling a sweep only makes sense with a single Nethermind client: nodes start one at a time
# and would otherwise overwrite each other's snapshots under a shared DIAG_DIR.
DOTTRACE="${DOTTRACE:-false}"
DOTTRACE_MODE="${DOTTRACE_MODE:-sampling}"
if [[ "$DOTTRACE" != "false" && "$CLIENTS" != "nethermind" ]]; then
  echo "::warning::dotTrace requested but CLIENTS='$CLIENTS' — profiling needs a lone nethermind client; disabling"
  DOTTRACE="false"
fi
sweep_dottrace="$DOTTRACE"
CORPUS_DIR="${CORPUS_DIR:-/mnt/sda/expb-data/rpc-bench}"
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
PARITY_STATE="$SCRATCH_ROOT/parity"

default_image() {
  case "$1" in
    geth) echo "ethereum/client-go:stable" ;;
    reth) echo "ghcr.io/paradigmxyz/reth:latest" ;;
    nethermind) echo "$NM_IMAGE" ;;
    *) echo "unknown client '$1'" >&2; return 1 ;;
  esac
}
snap_path() {
  if [[ "$1" == "nethermind" ]]; then
    if [[ "$STATE_LAYOUT" == "flat" ]]; then echo "/mnt/sda/nethermind-flat-${SNAPSHOT_BLOCK}"
    else echo "/mnt/sda/nethermind-${SNAPSHOT_BLOCK}"; fi
  else echo "/mnt/sda/$1-${SNAPSHOT_BLOCK}"; fi
}
layout_flags() { [[ "$1" == "nethermind" && "$STATE_LAYOUT" == "flat" ]] && echo "--FlatDb.Enabled=true" || true; }
isolation()    { [[ "$1" == "reth" ]] && echo "direct" || echo "overlay"; }

# One json-bench cell: $1=config (repo-relative) $2=rps $3=duration $4=out dir $5=client
# $6=label $7=corpus file (empty = normal cell; set = private corpus cell, aggregate-only output)
run_cell() {
  local cfg="$1" rps="$2" dur="$3" cell="$4" ctype="$5" label="$6" corpus="${7:-}"
  local is_corpus="false" deep="true"
  [[ -n "$corpus" ]] && { is_corpus="true"; deep="false"; }
  mkdir -p "$cell"
  OUT_DIR="$cell" RPC_URL="http://localhost:8545" CLIENT_TYPE="$ctype" LABEL="$label" \
    SCRATCH_ROOT="$SCRATCH_ROOT" JB_REF="$JB_REF" JB_MODE="benchmark" \
    JB_BENCHMARK_CONFIG="$cfg" JB_RPS="$rps" JB_DURATION="$dur" \
    JB_DEEP_CHECK="$deep" JB_HTML_REPORT="false" \
    JB_ETH_CALL_CORPUS="$is_corpus" JB_ETH_CALL_CORPUS_FILE="$corpus" \
    "$here/run-jsonbench.sh"
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

# Each entry is a client type or 'ctype@image' (e.g. nethermind@nethermindeth/nethermind:master) for
# same-client version comparisons. Sequential (one node up at a time), so same-snapshot variants are safe.
for entry in $CLIENTS; do
  ctype="${entry%%@*}"
  if [[ "$entry" == *@* ]]; then
    img="${entry#*@}"; label="${ctype}_$(printf '%s' "${img##*:}" | tr -c 'a-zA-Z0-9' '_')"
  else
    img="$(default_image "$ctype")" || { echo "skip $entry: no image"; continue; }; label="$ctype"
  fi
  docker pull "$img" >/dev/null 2>&1 || echo "pull failed — assuming $img is local"
  cst="$STATE_ROOT/$label"; mkdir -p "$cst"
  cname="rpcbench-sweep-${label}-${GITHUB_RUN_ID:-local}"
  echo "::group::sweep ${label} (type=${ctype}, image=${img}, db=$(snap_path "$ctype"), head=${SNAPSHOT_BLOCK})"
  if ! CLIENT="$ctype" INSTANCE="primary" NODE_IMAGE="$img" \
       DB_SOURCE="$(snap_path "$ctype")" DB_ISOLATION="$(isolation "$ctype")" \
       SCRATCH_ROOT="$SCRATCH_ROOT" STATE_DIR="$cst" NETWORK="$NETWORK" \
       JSONRPC_MODULES="$JSONRPC_MODULES" LAYOUT_FLAGS="$(layout_flags "$ctype")" \
       ADDITIONAL_FLAGS="" HEALTH_TIMEOUT="$HEALTH_TIMEOUT" \
       DOTTRACE="$sweep_dottrace" DOTTRACE_MODE="$DOTTRACE_MODE" DOTNET_TRACE="$sweep_dottrace" \
       RPC_GAS_CAP="$([[ "$JB_ETH_CALL_CORPUS" == "true" ]] && echo "$CORPUS_RPC_GAS_CAP")" \
       DIAG_DIR="$DIAG_DIR" CONTAINER_NAME="$cname" RPC_PORT="8545" \
       "$here/start-node.sh"; then
    echo "::warning::${label} failed to start — skipping its cells"; echo "::endgroup::"; continue
  fi
  LABELS+=("$label")

  if [[ "$JB_ETH_CALL_CORPUS" == "true" ]]; then
    # One latency cell per corpus per rps, then one full-corpus parity replay per corpus
    # while the node is still up. The first started client is the parity baseline.
    for corpus in "${CORPORA[@]}"; do
      clabel="$(corpus_label "$corpus")"
      # An empty rps_list runs no k6 cells: for a large corpus the JSON-array fixture alone can
      # exceed the box, and parity/timings do not need it.
      for rps in $RPS_LIST; do
        cell="$OUT_DIR/corpus/${clabel}/${label}/${rps}"
        cell_duration="$(corpus_cell_duration "$corpus" "$rps")"
        echo "-- CORPUS ${clabel} ${label} @ rps=${rps} for ${cell_duration} --"
        run_cell "$JB_BENCHMARK_CONFIG" "$rps" "$cell_duration" "$cell" "$ctype" "$label" "$corpus" \
          || { echo "::warning::corpus ${clabel}/${label}/${rps} failed"; cell_fail=$((cell_fail + 1)); }
        [[ -f "$cell/jsonbench-summary.md" ]] && SUMMARIES+=("iso|${clabel}|${label}|${rps}=$cell/jsonbench-summary.md")
      done
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
            --baseline-client "$BASELINE_LABEL" --candidate-client "$label"; then
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
            --rps "$CORPUS_TIMINGS_RPS" --concurrency "$CORPUS_TIMINGS_CONCURRENCY"; then
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
