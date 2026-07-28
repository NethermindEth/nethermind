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
  local cfg="$1" rps="$2" dur="$3" cell="$4" client="$5"
  mkdir -p "$cell"
  OUT_DIR="$cell" RPC_URL="http://localhost:8545" CLIENT_TYPE="$client" LABEL="$client" \
    SCRATCH_ROOT="$SCRATCH_ROOT" JB_REF="$JB_REF" JB_MODE="benchmark" \
    JB_BENCHMARK_CONFIG="$cfg" JB_RPS="$rps" JB_DURATION="$dur" \
    JB_DEEP_CHECK="true" JB_HTML_REPORT="false" \
    "$here/run-jsonbench.sh"
}

mkdir -p "$OUT_DIR" "$STATE_ROOT"
declare -a SUMMARIES=()

for client in $CLIENTS; do
  img="$(default_image "$client")" || { echo "skip $client: no image"; continue; }
  [[ "$client" != "nethermind" ]] && { docker pull "$img" || echo "pull failed — assuming $img is local"; }
  cst="$STATE_ROOT/$client"; mkdir -p "$cst"
  cname="rpcbench-sweep-${client}-${GITHUB_RUN_ID:-local}"
  echo "::group::sweep ${client} (image=${img}, db=$(snap_path "$client"), head=${SNAPSHOT_BLOCK})"
  if ! CLIENT="$client" INSTANCE="primary" NODE_IMAGE="$img" \
       DB_SOURCE="$(snap_path "$client")" DB_ISOLATION="$(isolation "$client")" \
       SCRATCH_ROOT="$SCRATCH_ROOT" STATE_DIR="$cst" NETWORK="$NETWORK" \
       JSONRPC_MODULES="$JSONRPC_MODULES" LAYOUT_FLAGS="$(layout_flags "$client")" \
       ADDITIONAL_FLAGS="" HEALTH_TIMEOUT="$HEALTH_TIMEOUT" DOTTRACE="false" \
       DIAG_DIR="$DIAG_DIR" CONTAINER_NAME="$cname" RPC_PORT="8545" \
       "$here/start-node.sh"; then
    echo "::warning::${client} failed to start — skipping its cells"; echo "::endgroup::"; continue
  fi

  for rps in $RPS_LIST; do
    # ISOLATED: each scenario alone
    for icfg in $ISO_CONFIGS; do
      scen="$(basename "$icfg" .yaml)"
      cell="$OUT_DIR/iso/${client}/${rps}/${scen}"
      echo "-- ISO ${client} ${scen} @ rps=${rps} --"
      run_cell "$icfg" "$rps" "$ISO_DURATION" "$cell" "$client" || echo "::warning::iso ${client}/${scen}/${rps} failed"
      [[ -f "$cell/jsonbench-summary.md" ]] && SUMMARIES+=("iso|${scen}|${client}|${rps}=$cell/jsonbench-summary.md")
    done
    # MIXED: all scenarios together
    mcell="$OUT_DIR/mix/${client}/${rps}"
    echo "-- MIX ${client} @ rps=${rps} --"
    run_cell "$JB_BENCHMARK_CONFIG" "$rps" "$JB_DURATION" "$mcell" "$client" || echo "::warning::mix ${client}/${rps} failed"
    [[ -f "$mcell/jsonbench-summary.md" ]] && SUMMARIES+=("mix|${client}|${rps}=$mcell/jsonbench-summary.md")
  done

  STATE_DIR="$cst" CONTAINER_NAME="$cname" OUT_DIR="$OUT_DIR" LOG_OUT="$cst/node.log" \
    "$here/stop-node.sh" || echo "::warning::stop-node failed for ${client}"
  echo "::endgroup::"
done

sink="${GITHUB_STEP_SUMMARY:-/dev/stdout}"
{
  echo "# Cross-client sweep — same head ${SNAPSHOT_BLOCK}"
  echo "_${#SUMMARIES[@]} cells · isolated dur ${ISO_DURATION}, mixed dur ${JB_DURATION} · json-bench ${JB_REF}_"
  echo
} >> "$sink"
if [[ "${#SUMMARIES[@]}" -gt 0 ]]; then
  python3 "$here/percat-matrix.py" "${SUMMARIES[@]}" >> "$sink" || echo "aggregation failed" >> "$sink"
else
  echo "No cell summaries produced — every client failed to start." >> "$sink"; exit 1
fi
