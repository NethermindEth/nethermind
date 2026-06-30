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

# Canonicalize (symlink-proof) and enforce DB_SOURCE / SCRATCH_ROOT sanity and
# disjointness — scratch is wiped on teardown and must never reach the snapshot.
guard_paths

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

# Cross-run anchor: compare against the fingerprint persisted by the last
# cleanly-verified run, so a mutation during a hard-interrupted run (where the
# verify step never executed) cannot be silently adopted as the new baseline.
# Drift is a warning, not an error — admins legitimately refresh snapshots.
ANCHOR_DIR="$SCRATCH_ROOT/fingerprints"
ANCHOR_FILE="$ANCHOR_DIR/$(basename "$DB_SOURCE").txt"
mkdir -p "$ANCHOR_DIR"
if [[ -f "$ANCHOR_FILE" ]]; then
  if [[ "$(head -n 1 "$ANCHOR_FILE")" == "$(head -n 1 "$STATE_DIR/db-baseline.txt")" ]] \
      && ! diff -q "$ANCHOR_FILE" "$STATE_DIR/db-baseline.txt" >/dev/null 2>&1; then
    log "::warning::Snapshot fingerprint differs from the last verified run's anchor ($ANCHOR_FILE). If the snapshot was not intentionally refreshed, a previous interrupted run may have modified it."
  fi
fi

# ---------------------------------------------------------------------------
# 2) Build an isolated, writable datadir view without touching the source.
# ---------------------------------------------------------------------------
# Stale containers from a hard-interrupted previous run would still hold the old
# overlay mount namespace and port 8545 — reap them before touching scratch.
reap_stale_containers "nethermind-rpcbench" "ethcallchaos-bench"

RUN_SCRATCH="$SCRATCH_ROOT/run"
# Unmount leftovers from an interrupted previous run before clearing scratch.
for m in "$RUN_SCRATCH/merged" "$RUN_SCRATCH/ro"; do
  if mountpoint -q "$m" 2>/dev/null; then
    as_root umount "$m" 2>/dev/null || as_root umount -l "$m" 2>/dev/null || true
  fi
done
assert_no_mounts_under "$RUN_SCRATCH"
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

# Persist state for teardown/verification NOW — if docker run fails below,
# stop-node.sh must still be able to verify the fingerprint and tear down the
# mount (docker logs/stop on a never-started container are harmless no-ops).
{
  echo "CONTAINER_NAME=$CONTAINER_NAME"
  echo "DB_ISOLATION=$DB_ISOLATION"
  echo "RUN_SCRATCH=$RUN_SCRATCH"
  echo "SCRATCH_ROOT=$SCRATCH_ROOT"
  echo "DB_SOURCE=$DB_SOURCE"
  echo "DIAG_DIR=$DIAG_DIR"
  echo "DOTTRACE=$DOTTRACE"
  echo "RPC_PORT=$RPC_PORT"
} > "$STATE_DIR/node.env"

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
entry_args=()
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
fi

# ---------------------------------------------------------------------------
# 4) Start the node.
# ---------------------------------------------------------------------------
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Starting Nethermind container '$CONTAINER_NAME'..."
log "  node args: ${node_args[*]}"
# ${arr[@]+...} keeps the empty-array expansion safe under set -u on bash < 4.4.
docker run "${docker_args[@]}" "$NETHERMIND_IMAGE" ${entry_args[@]+"${entry_args[@]}"} "${node_args[@]}"

# ---------------------------------------------------------------------------
# 5) Wait for the node to serve JSON-RPC.
# ---------------------------------------------------------------------------
wait_for_rpc "http://localhost:${RPC_PORT}" "$HEALTH_TIMEOUT" "$CONTAINER_NAME"
log "=== Node ready for benchmarking ==="
