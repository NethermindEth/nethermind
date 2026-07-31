#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Start an execution-client node (nethermind|geth|reth) for RPC benchmarking against an
# isolated view of a pristine DB snapshot, mirroring how expb uses the snapshots here.

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
# Identical eth_call gas cap across clients so a heavy call is served, not truncated —
# geth/reth default to 50M and Nethermind to 100M, making cross-client timings incomparable.
RPC_GAS_CAP="${RPC_GAS_CAP:-1000000000}"
LAYOUT_FLAGS="${LAYOUT_FLAGS:-}"                   # e.g. --FlatDb.Enabled=true for the flat snapshot (nethermind only)
ADDITIONAL_FLAGS="${ADDITIONAL_FLAGS:-}"

# The opcode histogram is driven by an environment variable, and additional_nethermind_flags is the
# only per-run string the workflow already plumbs down to here, so accept it as a pseudo-flag and
# translate it. Diagnostic branch only: costs ~4% when on, and nothing at all when absent.
OPCODE_HISTOGRAM_PATH=""
if [[ "$ADDITIONAL_FLAGS" == *--OpcodeHistogram=* ]]; then
  OPCODE_HISTOGRAM_PATH="$(printf '%s\n' "$ADDITIONAL_FLAGS" | sed -E 's/.*--OpcodeHistogram=([^ ]*).*/\1/')"
  ADDITIONAL_FLAGS="$(printf '%s\n' "$ADDITIONAL_FLAGS" | sed -E 's/--OpcodeHistogram=[^ ]*//')"
fi
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
if [[ "$DB_ISOLATION" == "direct" ]]; then
  log "Snapshot:   $DB_SOURCE  (READ-WRITE bind — direct mode)"
else
  log "Snapshot:   $DB_SOURCE  (READ-ONLY — will not be modified)"
fi
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

# 1) Tamper tripwire baseline of the pristine snapshot.
BASELINE_FILE="$STATE_DIR/db-baseline$SUFFIX.txt"
log "Computing DB integrity baseline (tamper tripwire)..."
db_fingerprint "$DB_SOURCE" "$BASELINE_FILE"
log "  baseline: $(wc -l < "$BASELINE_FILE") lines, sha256=$(sha256sum "$BASELINE_FILE" | cut -d' ' -f1)"

# Compare against the last cleanly-verified run's fingerprint so a mutation during a
# hard-interrupted run isn't silently adopted as baseline; drift warns (snapshots get refreshed).
ANCHOR_DIR="$SCRATCH_ROOT/fingerprints"
ANCHOR_FILE="$ANCHOR_DIR/$(basename "$DB_SOURCE").txt"
mkdir -p "$ANCHOR_DIR"
if [[ -f "$ANCHOR_FILE" ]]; then
  if [[ "$(head -n 1 "$ANCHOR_FILE")" == "$(head -n 1 "$BASELINE_FILE")" ]] \
      && ! diff -q "$ANCHOR_FILE" "$BASELINE_FILE" >/dev/null 2>&1; then
    log "::warning::Snapshot fingerprint differs from the last verified run's anchor ($ANCHOR_FILE). If the snapshot was not intentionally refreshed, a previous interrupted run may have modified it."
  fi
fi

# 2) Build an isolated, writable datadir view without touching the source.
# Reap stale containers (old overlay mount + ports 8545/8546) before touching scratch.
# Only primary reaps — reference starts second and must not kill this run's primary.
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
  direct)
    # Mount the snapshot read-write — the only mode avoiding overlayfs whole-file copy-up
    # (~200s for reth's mdbx.dat); snapshot is mutated, stop-node.sh warns. See README "direct".
    log "::warning::db_isolation=direct — mounting the pristine snapshot READ-WRITE; the node's startup writes will modify it (accepted tradeoff)."
    DATA_DIR_SOURCE="$DB_SOURCE"
    MOUNT_OPT="rw"
    ;;
  *)
    die "unknown DB_ISOLATION '$DB_ISOLATION' (expected overlay | copy | readonly-bind | direct)"
    ;;
esac

# geth backups hold the CONTENTS of <datadir>/geth, so mount one level down; geth runs
# with --datadir=$DATA_DIR_TARGET and finds $DATA_DIR_TARGET/geth/chaindata.
DATA_MOUNT_TARGET="$DATA_DIR_TARGET"
[[ "$CLIENT" == "geth" ]] && DATA_MOUNT_TARGET="$DATA_DIR_TARGET/geth"
log "  datadir view: $DATA_DIR_SOURCE  (mounted $MOUNT_OPT into container at $DATA_MOUNT_TARGET)"

# Persist state for teardown NOW — if docker run fails below, stop-node.sh must still
# verify the fingerprint and tear down the mount.
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

# 3) Assemble the node command.
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
      "--JsonRpc.GasCap=$RPC_GAS_CAP"
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
      "--rpc.gascap=$RPC_GAS_CAP"
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
      "--rpc.gascap=$RPC_GAS_CAP"
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
if [[ -n "$OPCODE_HISTOGRAM_PATH" ]]; then
  # The report has to land on a mounted path or it dies with the container. The diag dir is only
  # mounted by the dotTrace branch above, so mount it here too when dotTrace is off.
  if [[ "$DOTTRACE" != "true" ]]; then
    mkdir -p "$DIAG_DIR/dottrace"
    docker_args+=(-v "$DIAG_DIR/dottrace:/dottrace-output:rw")
  fi
  docker_args+=(-e "NETHERMIND_OPCODE_HISTOGRAM=$OPCODE_HISTOGRAM_PATH")
  log "Opcode histogram enabled, writing to $OPCODE_HISTOGRAM_PATH inside the container."
fi
# Two settings used to be forced on the Nethermind container here and both cost us on this
# workload. Disabling tiered compilation also disables Dynamic PGO, which the runner project asks
# for and which is what devirtualizes and inlines call-heavy code - an interpreter dispatching
# through function-pointer tables is exactly that. GC latency level 0 is the batch mode, trading
# pause length for throughput, and the measured p99-over-p50 spread was 1.57x against 1.07x for the
# reference client, which is tail, not compute. Neither was applied to the other clients, so leaving
# them off also makes the comparison symmetric. Set RPCBENCH_LEGACY_DOTNET_TUNING=1 to restore them.
if [[ "$CLIENT" == "nethermind" && "${RPCBENCH_LEGACY_DOTNET_TUNING:-}" == "1" ]]; then
  docker_args+=(
    -e "DOTNET_TieredCompilation=0"
    -e "DOTNET_GCLatencyLevel=0"
  )
fi
[[ -n "$NODE_CPUSET" ]] && docker_args+=(--cpuset-cpus "$NODE_CPUSET")
[[ -n "$NODE_MEMORY" ]] && docker_args+=(--memory "$NODE_MEMORY")

# dotTrace (nethermind only): mount the host CLI and wrap the node binary, as expb's
# --dottrace does. SIGINT (stop-signal) lets dotTrace finalize the .dtp.
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

# 4) Start the node.
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Starting $CLIENT container '$CONTAINER_NAME'..."
log "  node args: ${node_args[*]}"
# ${arr[@]+...} keeps the empty-array expansion safe under set -u on bash < 4.4.
docker run "${docker_args[@]}" "$NODE_IMAGE" ${entry_args[@]+"${entry_args[@]}"} "${node_args[@]}"

# 5) Wait for the node to serve JSON-RPC.
wait_for_rpc "http://localhost:${RPC_PORT}" "$HEALTH_TIMEOUT" "$CONTAINER_NAME"
log "=== Node ready for benchmarking ==="
