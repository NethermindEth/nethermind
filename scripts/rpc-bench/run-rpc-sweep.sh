#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Single-dispatch cross-client x rps sweep. One job loops CLIENTS x RPS_LIST with
# ONE node up at a time (clean latency, no cross-node contention) and ALL clients
# pinned to the same SNAPSHOT_BLOCK, so the comparison is apples-to-apples — the
# thing a per-client, different-head run gets wrong. deep_check is on so each
# cell captures raw responses for offline cross-client parity. Reuses
# start-node.sh / run-jsonbench.sh / stop-node.sh and aggregates every cell's
# jsonbench-summary.md into one matrix on $GITHUB_STEP_SUMMARY.
#
# A client whose node fails to start is skipped (its cells are absent from the
# matrix) rather than failing the whole sweep — a 3x3 grid shouldn't die on one
# bad node.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

: "${OUT_DIR:?base output directory for per-cell results}"
: "${STATE_ROOT:?base directory for per-client node state}"
: "${SCRATCH_ROOT:?writable scratch root on the snapshot disk}"
: "${NM_IMAGE:?built nethermind image ref (from the resolve/build step)}"
: "${SNAPSHOT_BLOCK:?shared head all clients are pinned to}"
: "${JB_REF:?json-bench ref}"
: "${JB_BENCHMARK_CONFIG:?benchmark config path (repo-relative to json-bench)}"

CLIENTS="${CLIENTS:-nethermind geth reth}"
RPS_LIST="${RPS_LIST:-100 250 500}"
STATE_LAYOUT="${STATE_LAYOUT:-flat}"
JB_DURATION="${JB_DURATION:-60s}"
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
  else
    echo "/mnt/sda/$1-${SNAPSHOT_BLOCK}"
  fi
}
layout_flags() { [[ "$1" == "nethermind" && "$STATE_LAYOUT" == "flat" ]] && echo "--FlatDb.Enabled=true" || true; }
isolation()    { [[ "$1" == "reth" ]] && echo "direct" || echo "overlay"; }

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
    echo "::warning::${client} failed to start — skipping its cells"
    echo "::endgroup::"
    continue
  fi

  for rps in $RPS_LIST; do
    cell="$OUT_DIR/${client}-${rps}"; mkdir -p "$cell"
    echo "-- cell ${client} @ rps=${rps} --"
    OUT_DIR="$cell" RPC_URL="http://localhost:8545" CLIENT_TYPE="$client" LABEL="${client}_${rps}" \
      SCRATCH_ROOT="$SCRATCH_ROOT" JB_REF="$JB_REF" JB_MODE="benchmark" \
      JB_BENCHMARK_CONFIG="$JB_BENCHMARK_CONFIG" JB_RPS="$rps" JB_DURATION="$JB_DURATION" \
      JB_DEEP_CHECK="true" JB_HTML_REPORT="false" \
      "$here/run-jsonbench.sh" || echo "::warning::cell ${client}-${rps} failed"
    [[ -f "$cell/jsonbench-summary.md" ]] && SUMMARIES+=("${client}:${rps}=$cell/jsonbench-summary.md")
  done

  STATE_DIR="$cst" CONTAINER_NAME="$cname" OUT_DIR="$OUT_DIR" LOG_OUT="$cst/node.log" \
    "$here/stop-node.sh" || echo "::warning::stop-node failed for ${client}"
  echo "::endgroup::"
done

# ---- Aggregate every cell into one matrix on the job summary ----
sink="${GITHUB_STEP_SUMMARY:-/dev/stdout}"
{
  echo "# Cross-client x rps sweep — same head ${SNAPSHOT_BLOCK}"
  echo "_config: ${JB_BENCHMARK_CONFIG} @ json-bench ${JB_REF}, ${#SUMMARIES[@]} cells_"
  echo
} >> "$sink"
if [[ "${#SUMMARIES[@]}" -gt 0 ]]; then
  python3 "$here/percat-matrix.py" "${SUMMARIES[@]}" >> "$sink" || echo "aggregation failed" >> "$sink"
else
  echo "No cell summaries produced — every client failed to start." >> "$sink"
  exit 1
fi
