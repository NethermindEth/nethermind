#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Start a Nethermind node for RPC benchmarking against an ISOLATED view of a
# pristine DB snapshot, mirroring how expb (execution-payloads-benchmarks) uses
# the same snapshot on this runner:
#   * the snapshot is a Nethermind DATADIR (contains <network>/ chain DB) and is
#     bound to /execution-data; the node runs --datadir=/execution-data,
#   * isolation backend 'overlay' matches expb's snapshot_backend: overlay
#     (read-only lowerdir + scratch upperdir, redirect_dir/metacopy/volatile),
#   * dotTrace profiling mounts the host-installed dottrace CLI into the
#     container and wraps the entrypoint — works with ANY image (expb's --dottrace).
# The pristine snapshot is NEVER mounted writable. A tamper tripwire baseline is
# captured here and verified by stop-node.sh.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${DB_SOURCE:?path to the pristine Nethermind datadir snapshot (e.g. /mnt/sda/nethermind-flat-snapshot)}"
: "${SCRATCH_ROOT:?writable scratch root on the same large disk as the snapshot}"
: "${STATE_DIR:?directory to persist node state for stop-node.sh}"
: "${NETHERMIND_IMAGE:?docker image reference to run}"

DB_ISOLATION="${DB_ISOLATION:-overlay}"            # overlay | copy | readonly-bind
DATA_DIR_TARGET="${DATA_DIR_TARGET:-/execution-data}"
CONTAINER_NAME="${CONTAINER_NAME:-nethermind-rpcbench}"
RPC_PORT="${RPC_PORT:-8545}"
NETWORK="${NETWORK:-mainnet}"
DOTTRACE="${DOTTRACE:-false}"
DOTTRACE_HOST_PATH="${DOTTRACE_HOST_PATH:-/opt/dottrace}"
DIAG_DIR="${DIAG_DIR:-$SCRATCH_ROOT/diag}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-1800}"
JSONRPC_MODULES="${JSONRPC_MODULES:-Eth,Subscribe,Trace,TxPool,Web3,Personal,Proof,Net,Parity,Health,Rpc,Debug,Admin}"
LAYOUT_FLAGS="${LAYOUT_FLAGS:-}"                   # e.g. --FlatDb.Enabled=true for the flat snapshot
ADDITIONAL_FLAGS="${ADDITIONAL_FLAGS:-}"
NODE_CPUSET="${NODE_CPUSET:-}"                     # e.g. 2-7,10-15 (expb pins the client to these cores)
NODE_MEMORY="${NODE_MEMORY:-}"                     # e.g. 64g

mkdir -p "$STATE_DIR"
[[ -d "$DB_SOURCE" ]] || {
  log "DB_SOURCE '$DB_SOURCE' is not a directory. Snapshot candidates on this runner:"
  ls -1d /mnt/*/[Nn]ethermind*snapshot* /mnt/*/*/[Nn]ethermind*snapshot* 2>/dev/null | sed 's/^/  /' || echo "  <none found under /mnt>"
  die "set node_config.db_source to a valid snapshot path"
}

# Safety: the scratch tree is wiped on teardown, so the pristine snapshot must
# not live inside it (and vice versa, to keep isolation paths disjoint).
case "$DB_SOURCE/" in
  "$SCRATCH_ROOT"/*) die "DB_SOURCE must not be inside SCRATCH_ROOT — scratch is wiped on teardown" ;;
esac
case "$SCRATCH_ROOT/" in
  "$DB_SOURCE"/*) die "SCRATCH_ROOT must not be inside DB_SOURCE" ;;
esac

log "=== RPC benchmark node startup ==="
log "Image:      $NETHERMIND_IMAGE"
log "Snapshot:   $DB_SOURCE  (READ-ONLY — will not be modified)"
log "Isolation:  $DB_ISOLATION"
log "Scratch:    $SCRATCH_ROOT"
log "dotTrace:   $DOTTRACE"
log "RPC port:   $RPC_PORT  (network: $NETWORK)"

# ---------------------------------------------------------------------------
# 1) Tamper tripwire baseline of the pristine snapshot.
# ---------------------------------------------------------------------------
log "Computing DB integrity baseline (tamper tripwire)..."
db_fingerprint "$DB_SOURCE" "$STATE_DIR/db-baseline.txt"
log "  baseline: $(wc -l < "$STATE_DIR/db-baseline.txt") lines, sha256=$(sha256sum "$STATE_DIR/db-baseline.txt" | cut -d' ' -f1)"

# ---------------------------------------------------------------------------
# 2) Build an isolated, writable datadir view without touching the source.
# ---------------------------------------------------------------------------
RUN_SCRATCH="$SCRATCH_ROOT/run"
as_root rm -rf "$RUN_SCRATCH"
mkdir -p "$RUN_SCRATCH" "$DIAG_DIR"

case "$DB_ISOLATION" in
  overlay)
    mkdir -p "$RUN_SCRATCH/upper" "$RUN_SCRATCH/work" "$RUN_SCRATCH/merged"
    log "Mounting overlayfs (lowerdir=read-only source, upperdir=scratch)..."
    # Same options expb uses on this runner; fall back to plain options for
    # kernels without redirect_dir/metacopy support.
    as_root mount -t overlay overlay \
      -o "lowerdir=$DB_SOURCE,upperdir=$RUN_SCRATCH/upper,workdir=$RUN_SCRATCH/work,redirect_dir=on,metacopy=on,volatile" \
      "$RUN_SCRATCH/merged" \
      || as_root mount -t overlay overlay \
        -o "lowerdir=$DB_SOURCE,upperdir=$RUN_SCRATCH/upper,workdir=$RUN_SCRATCH/work" \
        "$RUN_SCRATCH/merged" \
      || die "overlay mount failed — ensure the runner allows mount and supports overlayfs, or pick db_isolation=copy"
    DATA_DIR_SOURCE="$RUN_SCRATCH/merged"
    MOUNT_OPT="rw"
    ;;
  copy)
    log "Copying snapshot to scratch (CoW reflink when the filesystem supports it)..."
    mkdir -p "$RUN_SCRATCH/db"
    cp -a --reflink=auto "$DB_SOURCE/." "$RUN_SCRATCH/db/"
    DATA_DIR_SOURCE="$RUN_SCRATCH/db"
    MOUNT_OPT="rw"
    ;;
  readonly-bind)
    log "Read-only bind mount of source (node/RocksDB must support read-only open)..."
    mkdir -p "$RUN_SCRATCH/ro"
    as_root mount --bind "$DB_SOURCE" "$RUN_SCRATCH/ro"
    as_root mount -o remount,ro,bind "$RUN_SCRATCH/ro"
    DATA_DIR_SOURCE="$RUN_SCRATCH/ro"
    MOUNT_OPT="ro"
    ;;
  *)
    die "unknown DB_ISOLATION '$DB_ISOLATION' (expected overlay | copy | readonly-bind)"
    ;;
esac
log "  datadir view: $DATA_DIR_SOURCE  (mounted $MOUNT_OPT into container at $DATA_DIR_TARGET)"

# ---------------------------------------------------------------------------
# 3) Assemble the node command (mirrors expb's NethermindConfig).
# ---------------------------------------------------------------------------
node_args=(
  "--datadir=$DATA_DIR_TARGET"
  "--config=$NETWORK"
  "--Init.BaseDbPath=$NETWORK"
  "--JsonRpc.Enabled=true"
  "--JsonRpc.Host=0.0.0.0"
  "--JsonRpc.Port=8545"
  "--JsonRpc.EnabledModules=$JSONRPC_MODULES"
  "--JsonRpc.Timeout=600000"
  # Park the node at the snapshot head: no peers, no discovery, no sync writes.
  "--Init.DiscoveryEnabled=false"
  "--Network.MaxActivePeers=0"
  # expb's stability flags: no forced GC between blocks, no background pruning.
  "--Merge.SweepMemory=NoGC"
  "--Merge.CompactMemory=No"
  "--Merge.CollectionsPerDecommit=-1"
  "--Pruning.Mode=None"
  "--HealthChecks.Enabled=false"
  "--Metrics.Enabled=false"
)
# shellcheck disable=SC2206
node_args+=($LAYOUT_FLAGS)
# shellcheck disable=SC2206
node_args+=($ADDITIONAL_FLAGS)

docker_args=(
  -d --name "$CONTAINER_NAME"
  --restart no
  --stop-signal SIGINT
  -p "${RPC_PORT}:8545"
  -v "$DATA_DIR_SOURCE:$DATA_DIR_TARGET:$MOUNT_OPT"
  -e "DOTNET_TieredCompilation=0"
  -e "DOTNET_GCLatencyLevel=0"
)
[[ -n "$NODE_CPUSET" ]] && docker_args+=(--cpuset-cpus "$NODE_CPUSET")
[[ -n "$NODE_MEMORY" ]] && docker_args+=(--memory "$NODE_MEMORY")

# dotTrace: mount the host-installed CLI and wrap the node binary, exactly as
# expb's --dottrace does. SIGINT (stop-signal) lets dotTrace finalize the .dtp.
if [[ "$DOTTRACE" == "true" ]]; then
  if [[ ! -x "$DOTTRACE_HOST_PATH/dottrace" ]]; then
    log "dotTrace CLI not found at $DOTTRACE_HOST_PATH — installing via dotnet tool..."
    dotnet tool install --tool-path "$DOTTRACE_HOST_PATH" JetBrains.dotTrace.GlobalTools \
      || as_root dotnet tool install --tool-path "$DOTTRACE_HOST_PATH" JetBrains.dotTrace.GlobalTools \
      || die "failed to install dotTrace CLI (is the .NET SDK on the runner?)"
  fi
  mkdir -p "$DIAG_DIR/dottrace"
  docker_args+=(
    -v "$DOTTRACE_HOST_PATH:/opt/dottrace:ro"
    -v "$DIAG_DIR/dottrace:/dottrace-output:rw"
    --entrypoint /opt/dottrace/dottrace
  )
  entry_args=(start --framework=NetCore "--save-to=/dottrace-output/rpcbench-${NETWORK}.dtp" --propagate-exit-code -- /nethermind/nethermind)
else
  # Run the binary directly (as expb does) — skips entrypoint.sh host tuning.
  docker_args+=(--entrypoint /nethermind/nethermind)
  entry_args=()
fi

# ---------------------------------------------------------------------------
# 4) Start the node.
# ---------------------------------------------------------------------------
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Starting Nethermind container '$CONTAINER_NAME'..."
log "  node args: ${node_args[*]}"
docker run "${docker_args[@]}" "$NETHERMIND_IMAGE" "${entry_args[@]}" "${node_args[@]}"

# Persist state for teardown / verification.
{
  echo "CONTAINER_NAME=$CONTAINER_NAME"
  echo "DB_ISOLATION=$DB_ISOLATION"
  echo "RUN_SCRATCH=$RUN_SCRATCH"
  echo "DB_SOURCE=$DB_SOURCE"
  echo "DIAG_DIR=$DIAG_DIR"
  echo "DOTTRACE=$DOTTRACE"
  echo "RPC_PORT=$RPC_PORT"
} > "$STATE_DIR/node.env"

# ---------------------------------------------------------------------------
# 5) Wait for the node to serve JSON-RPC.
# ---------------------------------------------------------------------------
sleep 5
if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
  log "Container exited prematurely. Last 100 log lines:"
  docker logs "$CONTAINER_NAME" 2>&1 | tail -n 100 || true
  die "node container failed to start"
fi

wait_for_rpc "http://localhost:${RPC_PORT}" "$HEALTH_TIMEOUT"
log "=== Node ready for benchmarking ==="
