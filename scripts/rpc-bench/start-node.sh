#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Start an execution-client node (nethermind | geth | reth) for RPC benchmarking
# against an ISOLATED view of a pristine DB snapshot, mirroring how expb
# (execution-payloads-benchmarks) uses the Nethermind snapshots on this runner:
#   * the snapshot is the client's DATADIR and is bound to /execution-data
#     (geth backups hold the contents of <datadir>/geth and are mounted one
#     level down so geth finds <datadir>/geth/chaindata),
#   * isolation backend 'overlay' matches expb's snapshot_backend: overlay
#     (read-only lowerdir + scratch upperdir, redirect_dir/metacopy/volatile),
#   * dotTrace profiling (nethermind only — it is .NET-specific) mounts the
#     host-installed dottrace CLI into the container and wraps the entrypoint.
# INSTANCE=reference starts a second, independently-isolated node (own scratch,
# state file and fingerprint) for cross-client comparison runs.
# The pristine snapshot is NEVER mounted writable. A tamper tripwire baseline is
# captured here and verified by stop-node.sh.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${DB_SOURCE:?path to the pristine client datadir snapshot (e.g. /mnt/sda/nethermind-flat-snapshot)}"
: "${SCRATCH_ROOT:?writable scratch root on the same large disk as the snapshot}"
: "${STATE_DIR:?directory to persist node state for stop-node.sh}"

CLIENT="${CLIENT:-nethermind}"                     # nethermind | geth | reth
INSTANCE="${INSTANCE:-primary}"                    # primary | reference
NODE_IMAGE="${NODE_IMAGE:-${NETHERMIND_IMAGE:-}}"  # NETHERMIND_IMAGE kept as an alias
[[ -n "$NODE_IMAGE" ]] || die "NODE_IMAGE (docker image reference to run) is required"

case "$INSTANCE" in
  primary)   SUFFIX="" ;;
  reference) SUFFIX="-reference" ;;
  *) die "unknown INSTANCE '$INSTANCE' (expected primary | reference)" ;;
esac
case "$CLIENT" in
  nethermind|geth|reth) ;;
  *) die "unknown CLIENT '$CLIENT' (expected nethermind | geth | reth)" ;;
esac

DB_ISOLATION="${DB_ISOLATION:-overlay}"            # overlay | copy | readonly-bind
DATA_DIR_TARGET="${DATA_DIR_TARGET:-/execution-data}"
CONTAINER_NAME="${CONTAINER_NAME:-rpcbench-$INSTANCE}"
RPC_PORT="${RPC_PORT:-8545}"
NETWORK="${NETWORK:-mainnet}"
DOTTRACE="${DOTTRACE:-false}"
DOTTRACE_HOST_PATH="${DOTTRACE_HOST_PATH:-/opt/dottrace}"
DIAG_DIR="${DIAG_DIR:-$SCRATCH_ROOT/diag}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-1800}"
# No Personal/Admin (or geth admin) by default — the RPC port is only ever
# served for the local load generator; administrative modules are not benchmarked.
JSONRPC_MODULES="${JSONRPC_MODULES:-Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug}"
GETH_HTTP_API="${GETH_HTTP_API:-eth,net,web3,debug,txpool}"
RETH_HTTP_API="${RETH_HTTP_API:-eth,net,web3,debug,trace,txpool}"
LAYOUT_FLAGS="${LAYOUT_FLAGS:-}"                   # e.g. --FlatDb.Enabled=true for the flat snapshot (nethermind only)
ADDITIONAL_FLAGS="${ADDITIONAL_FLAGS:-}"
NODE_CPUSET="${NODE_CPUSET:-}"                     # e.g. 2-7,10-15 (expb pins the client to these cores)
NODE_MEMORY="${NODE_MEMORY:-}"                     # e.g. 64g

if [[ "$DOTTRACE" == "true" && "$CLIENT" != "nethermind" ]]; then
  die "dottrace profiling requires CLIENT=nethermind (dotTrace is .NET-specific)"
fi

mkdir -p "$STATE_DIR"
[[ -d "$DB_SOURCE" ]] || {
  log "DB_SOURCE '$DB_SOURCE' is not a directory. Snapshot candidates on this runner:"
  ls -1d /mnt/*/[Nn]ethermind*snapshot* /mnt/*/*/[Nn]ethermind*snapshot* \
         /mnt/*/nethermind-* /mnt/*/geth-* /mnt/*/reth-* 2>/dev/null \
    | sort -u | sed 's/^/  /' || echo "  <none found under /mnt>"
  die "set node_config.db_source to a valid snapshot path"
}

# Canonicalize (symlink-proof) and enforce DB_SOURCE / SCRATCH_ROOT sanity and
# disjointness — scratch is wiped on teardown and must never reach the snapshot.
guard_paths

log "=== RPC benchmark node startup ==="
log "Client:     $CLIENT  (instance: $INSTANCE)"
log "Image:      $NODE_IMAGE"
log "Snapshot:   $DB_SOURCE  (READ-ONLY — will not be modified)"
log "Isolation:  $DB_ISOLATION"
log "Scratch:    $SCRATCH_ROOT"
log "dotTrace:   $DOTTRACE"
log "RPC port:   $RPC_PORT  (network: $NETWORK)"
# Snapshot sets carry provenance sidecars (capture head + client version) — log
# them so a mismatched snapshot/image pairing is visible in the run log.
for f in _snapshot_metadata.json _snapshot_web3_clientVersion.json; do
  if [[ -f "$DB_SOURCE/$f" ]]; then
    log "  $f: $(tr -d '\n' < "$DB_SOURCE/$f" | head -c 300)"
  fi
done

# ---------------------------------------------------------------------------
# 1) Tamper tripwire baseline of the pristine snapshot.
# ---------------------------------------------------------------------------
BASELINE_FILE="$STATE_DIR/db-baseline$SUFFIX.txt"
log "Computing DB integrity baseline (tamper tripwire)..."
db_fingerprint "$DB_SOURCE" "$BASELINE_FILE"
log "  baseline: $(wc -l < "$BASELINE_FILE") lines, sha256=$(sha256sum "$BASELINE_FILE" | cut -d' ' -f1)"

# Cross-run anchor: compare against the fingerprint persisted by the last
# cleanly-verified run, so a mutation during a hard-interrupted run (where the
# verify step never executed) cannot be silently adopted as the new baseline.
# Drift is a warning, not an error — admins legitimately refresh snapshots.
ANCHOR_DIR="$SCRATCH_ROOT/fingerprints"
ANCHOR_FILE="$ANCHOR_DIR/$(basename "$DB_SOURCE").txt"
mkdir -p "$ANCHOR_DIR"
if [[ -f "$ANCHOR_FILE" ]]; then
  if [[ "$(head -n 1 "$ANCHOR_FILE")" == "$(head -n 1 "$BASELINE_FILE")" ]] \
      && ! diff -q "$ANCHOR_FILE" "$BASELINE_FILE" >/dev/null 2>&1; then
    log "::warning::Snapshot fingerprint differs from the last verified run's anchor ($ANCHOR_FILE). If the snapshot was not intentionally refreshed, a previous interrupted run may have modified it."
  fi
fi

# ---------------------------------------------------------------------------
# 2) Build an isolated, writable datadir view without touching the source.
# ---------------------------------------------------------------------------
# Stale containers from a hard-interrupted previous run would still hold the old
# overlay mount namespace and ports 8545/8546 — reap them before touching
# scratch. Only the primary instance reaps: the reference node starts second and
# must not kill the just-started primary of the SAME run.
if [[ "$INSTANCE" == "primary" ]]; then
  reap_stale_containers "rpcbench-" "nethermind-rpcbench" "ethcallchaos-bench" "jsonbench-"
fi

RUN_SCRATCH="$SCRATCH_ROOT/run$SUFFIX"
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
    log "Read-only bind mount of source (node/DB engine must support read-only open)..."
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

# The geth backups on this runner hold the CONTENTS of <datadir>/geth
# (chaindata, triedb, ...), so mount them one level down; geth then runs with
# --datadir=$DATA_DIR_TARGET and finds $DATA_DIR_TARGET/geth/chaindata.
DATA_MOUNT_TARGET="$DATA_DIR_TARGET"
[[ "$CLIENT" == "geth" ]] && DATA_MOUNT_TARGET="$DATA_DIR_TARGET/geth"
log "  datadir view: $DATA_DIR_SOURCE  (mounted $MOUNT_OPT into container at $DATA_MOUNT_TARGET)"

# Persist state for teardown/verification NOW — if docker run fails below,
# stop-node.sh must still be able to verify the fingerprint and tear down the
# mount (docker logs/stop on a never-started container are harmless no-ops).
{
  echo "CLIENT=$CLIENT"
  echo "INSTANCE=$INSTANCE"
  echo "INSTANCE_SUFFIX=$SUFFIX"
  echo "CONTAINER_NAME=$CONTAINER_NAME"
  echo "DB_ISOLATION=$DB_ISOLATION"
  echo "RUN_SCRATCH=$RUN_SCRATCH"
  echo "SCRATCH_ROOT=$SCRATCH_ROOT"
  echo "DB_SOURCE=$DB_SOURCE"
  echo "DIAG_DIR=$DIAG_DIR"
  echo "DOTTRACE=$DOTTRACE"
  echo "RPC_PORT=$RPC_PORT"
} > "$STATE_DIR/node$SUFFIX.env"

# ---------------------------------------------------------------------------
# 3) Assemble the node command.
# ---------------------------------------------------------------------------
case "$CLIENT" in
  nethermind)
    # Mirrors expb's NethermindConfig.
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
    ;;
  geth)
    # The official image defaults to mainnet; other networks would need a
    # network-flag mapping — add it when a non-mainnet snapshot exists.
    [[ "$NETWORK" == "mainnet" ]] || die "CLIENT=geth supports only network=mainnet (got '$NETWORK')"
    node_args=(
      "--datadir=$DATA_DIR_TARGET"
      "--http" "--http.addr=0.0.0.0" "--http.port=8545"
      "--http.api=$GETH_HTTP_API"
      "--http.vhosts=*"
      # Park the node at the snapshot head: no peers, no discovery.
      "--nodiscover" "--maxpeers=0"
      "--ipcdisable"
    )
    ;;
  reth)
    [[ "$NETWORK" == "mainnet" ]] || die "CLIENT=reth supports only network=mainnet (got '$NETWORK')"
    node_args=(
      node
      "--datadir=$DATA_DIR_TARGET"
      "--http" "--http.addr=0.0.0.0" "--http.port=8545"
      "--http.api=$RETH_HTTP_API"
      # Park the node at the snapshot head: no peers, no discovery.
      "--disable-discovery" "--max-outbound-peers=0" "--max-inbound-peers=0"
    )
    ;;
esac
# shellcheck disable=SC2206
node_args+=($ADDITIONAL_FLAGS)

docker_args=(
  -d --name "$CONTAINER_NAME"
  --restart no
  --stop-signal SIGINT
  # Loopback-only: the load generators run on this host; publishing on all
  # interfaces would let other network hosts hit the node mid-benchmark.
  -p "127.0.0.1:${RPC_PORT}:8545"
  -v "$DATA_DIR_SOURCE:$DATA_MOUNT_TARGET:$MOUNT_OPT"
)
if [[ "$CLIENT" == "nethermind" ]]; then
  docker_args+=(
    -e "DOTNET_TieredCompilation=0"
    -e "DOTNET_GCLatencyLevel=0"
  )
fi
[[ -n "$NODE_CPUSET" ]] && docker_args+=(--cpuset-cpus "$NODE_CPUSET")
[[ -n "$NODE_MEMORY" ]] && docker_args+=(--memory "$NODE_MEMORY")

# dotTrace (nethermind only): mount the host-installed CLI and wrap the node
# binary, exactly as expb's --dottrace does. SIGINT (stop-signal) lets dotTrace
# finalize the .dtp.
entry_args=()
if [[ "$DOTTRACE" == "true" ]]; then
  if [[ ! -x "$DOTTRACE_HOST_PATH/dottrace" ]]; then
    log "dotTrace CLI not found at $DOTTRACE_HOST_PATH — installing via dotnet tool..."
    dotnet tool install --tool-path "$DOTTRACE_HOST_PATH" JetBrains.dotTrace.GlobalTools \
      || as_root dotnet tool install --tool-path "$DOTTRACE_HOST_PATH" JetBrains.dotTrace.GlobalTools \
      || die "failed to install dotTrace CLI (is the .NET SDK on the runner?)"
  fi
  # A hard-interrupted previous run can leave snapshots here that the collector
  # would archive as if they came from THIS run — always start from an empty dir.
  assert_no_mounts_under "$DIAG_DIR/dottrace"
  as_root rm -rf "$DIAG_DIR/dottrace"
  mkdir -p "$DIAG_DIR/dottrace"
  docker_args+=(
    -v "$DOTTRACE_HOST_PATH:/opt/dottrace:ro"
    -v "$DIAG_DIR/dottrace:/dottrace-output:rw"
    --entrypoint /opt/dottrace/dottrace
  )
  entry_args=(start --framework=NetCore "--save-to=/dottrace-output/rpcbench-${NETWORK}${SUFFIX}.dtp" --propagate-exit-code -- /nethermind/nethermind)
elif [[ "$CLIENT" == "nethermind" ]]; then
  # Run the binary directly (as expb does) — skips entrypoint.sh host tuning.
  docker_args+=(--entrypoint /nethermind/nethermind)
fi
# geth/reth official images already have the client binary as their entrypoint;
# node_args are passed as the container command.

# ---------------------------------------------------------------------------
# 4) Start the node.
# ---------------------------------------------------------------------------
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Starting $CLIENT container '$CONTAINER_NAME'..."
log "  node args: ${node_args[*]}"
# ${arr[@]+...} keeps the empty-array expansion safe under set -u on bash < 4.4.
docker run "${docker_args[@]}" "$NODE_IMAGE" ${entry_args[@]+"${entry_args[@]}"} "${node_args[@]}"

# ---------------------------------------------------------------------------
# 5) Wait for the node to serve JSON-RPC.
# ---------------------------------------------------------------------------
wait_for_rpc "http://localhost:${RPC_PORT}" "$HEALTH_TIMEOUT" "$CONTAINER_NAME"
log "=== Node ready for benchmarking ==="
