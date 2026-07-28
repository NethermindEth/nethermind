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
RPS_LIST="${RPS_LIST:-100 250 500}"
ISO_CONFIGS="${ISO_CONFIGS:-}"          # space-separated repo-relative single-scenario configs; empty = mixed only
STATE_LAYOUT="${STATE_LAYOUT:-flat}"
JB_DURATION="${JB_DURATION:-60s}"       # mixed-run load duration
ISO_DURATION="${ISO_DURATION:-20s}"     # per-scenario isolated load duration (shorter; single call)
NETWORK="${NETWORK:-mainnet}"
JSONRPC_MODULES="${JSONRPC_MODULES:-Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-1800}"
DIAG_DIR="${DIAG_DIR:-$SCRATCH_ROOT/diag}"

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
run_cell() {
  local cfg="$1" rps="$2" dur="$3" cell="$4" ctype="$5" label="$6"
  mkdir -p "$cell"
  OUT_DIR="$cell" RPC_URL="http://localhost:8545" CLIENT_TYPE="$ctype" LABEL="$label" \
    SCRATCH_ROOT="$SCRATCH_ROOT" JB_REF="$JB_REF" JB_MODE="benchmark" \
    JB_BENCHMARK_CONFIG="$cfg" JB_RPS="$rps" JB_DURATION="$dur" \
    JB_DEEP_CHECK="true" JB_HTML_REPORT="false" \
    "$here/run-jsonbench.sh"
}

mkdir -p "$OUT_DIR" "$STATE_ROOT"
declare -a SUMMARIES=()
declare -a LABELS=()
node_issue=0
cell_fail=0   # load-test cells that ran but failed (distinct from a client skipped for never starting)
stop_fail=0   # stop-node.sh reported a DB-integrity/teardown failure (overlay clients; direct only warns)

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
       ADDITIONAL_FLAGS="" HEALTH_TIMEOUT="$HEALTH_TIMEOUT" DOTTRACE="false" \
       DIAG_DIR="$DIAG_DIR" CONTAINER_NAME="$cname" RPC_PORT="8545" \
       "$here/start-node.sh"; then
    echo "::warning::${label} failed to start — skipping its cells"; echo "::endgroup::"; continue
  fi
  LABELS+=("$label")

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

  # stop-node.sh verifies the snapshot is pristine and exits non-zero on a DB-integrity/teardown failure. That must fail
  # the sweep — not degrade to a warning. reth 'direct' legitimately mutates and stop-node warns-not-fails, so this only
  # trips overlay clients.
  if ! STATE_DIR="$cst" CONTAINER_NAME="$cname" OUT_DIR="$OUT_DIR" LOG_OUT="$cst/node.log" \
       "$here/stop-node.sh"; then
    echo "::error::${label}: stop-node failed (DB integrity check or teardown) — failing the sweep"; stop_fail=1
  fi
  # Sweep mode isn't covered by the workflow's log-scan step, so scan each node log here with the same four checks.
  if [[ -f "$cst/node.log" ]]; then
    clean="$cst/node.clean.log"
    sed -E 's/\x1B\[[0-9;?]*[ -/]*[@-~]//g' "$cst/node.log" > "$clean"
    grep -in "Exception" "$clean" | grep -vF 'Incorrect JSON RPC parameters' > "$cst/node.exc" || true
    if [[ "$ctype" == "nethermind" ]]; then
      # Exception / invalid-block / shutdown-marker wording is Nethermind-specific — gate only on NM cells.
      if [[ -s "$cst/node.exc" ]]; then echo "::warning::${label}: Exception(s) in node log:"; head -20 "$cst/node.exc"; node_issue=1; fi
      if grep -qEi 'invalid[[:space:]_-]*block' "$clean"; then echo "::warning::${label}: invalid block in node log"; node_issue=1; fi
      # A missing marker means docker SIGKILLed a hung node or shutdown crashed — run untrustworthy.
      if ! grep -q "Nethermind is shut down" "$clean"; then
        echo "::warning::${label}: 'Nethermind is shut down' marker not found — node did not shut down cleanly"; node_issue=1
      fi
    elif [[ -s "$cst/node.exc" ]]; then
      # geth/reth: NM wording false-positives, so warn only — don't gate on the reference clients.
      echo "::warning::${label}: Exception-like lines in node log (warn only, non-Nethermind):"; head -20 "$cst/node.exc"
    fi
    # Severe patterns: warn-only for every client (mirrors the workflow's non-gating scan).
    for pattern in "Unhandled" "Fatal" "ERROR"; do
      if grep -qi "$pattern" "$clean"; then
        echo "::warning::${label}: severe log pattern '$pattern' (first 10):"; grep -in "$pattern" "$clean" | head -10 || true
      fi
    done
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
exit "$fail"
