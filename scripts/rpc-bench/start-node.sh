#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Start an execution client (nethermind|geth|reth) on an isolated view of a pristine DB snapshot.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${DB_SOURCE:?path to the pristine client datadir snapshot}"
: "${SCRATCH_ROOT:?writable scratch root on the same disk as the snapshot}"
: "${STATE_DIR:?directory to persist node state for stop-node.sh}"

CLIENT="${CLIENT:-nethermind}"
INSTANCE="${INSTANCE:-primary}"
NODE_IMAGE="${NODE_IMAGE:-${NETHERMIND_IMAGE:-}}"
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

DB_ISOLATION="${DB_ISOLATION:-overlay}"
DATA_DIR_TARGET="${DATA_DIR_TARGET:-/execution-data}"
CONTAINER_NAME="${CONTAINER_NAME:-rpcbench-$INSTANCE}"
RPC_PORT="${RPC_PORT:-8545}"
NETWORK="${NETWORK:-mainnet}"
DOTTRACE="${DOTTRACE:-false}"
DOTTRACE_MODE="${DOTTRACE_MODE:-sampling}"
DOTTRACE_HOST_PATH="${DOTTRACE_HOST_PATH:-/opt/dottrace}"
DIAG_DIR="${DIAG_DIR:-$SCRATCH_ROOT/diag}"
PERF="${PERF:-false}"
# perf samples on the host: it is absent from the client images and links against
# libLLVM/libpython/libtraceevent, so the host binary cannot be mounted in.
PERF_FREQUENCY="${PERF_FREQUENCY:-99}"
# dotnet-trace EventPipe sidecar (runtime events: GC, contention, threading, exceptions). The tool is
# mounted from the host and attached inside the container by start_profilers, so it never sees the
# warm-up; see lib.sh. DOTNET_TRACE_MAX_SECONDS optionally caps the session.
DOTNET_TRACE="${DOTNET_TRACE:-false}"
DOTNET_TRACE_HOST_PATH="${DOTNET_TRACE_HOST_PATH:-/opt/dotnet-trace}"
# Pinned like every other tool on this rig: an unpinned install would drift the collector between
# runs whose numbers are meant to be comparable.
DOTNET_TRACE_VERSION="${DOTNET_TRACE_VERSION:-9.0.661903}"
# true = leave perf unstarted and dotTrace launched with data collection off; the workflow runs
# start-profilers.sh once the warm-up is done, so the profiles cover only the measured phase.
PROFILE_AFTER_WARMUP="${PROFILE_AFTER_WARMUP:-false}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-1800}"
JSONRPC_MODULES="${JSONRPC_MODULES:-Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug}"
GETH_HTTP_API="${GETH_HTTP_API:-eth,net,web3,debug,txpool}"
RETH_HTTP_API="${RETH_HTTP_API:-eth,net,web3,debug,trace,txpool}"
RPC_GAS_CAP="${RPC_GAS_CAP:-1000000000}"
LAYOUT_FLAGS="${LAYOUT_FLAGS:-}"
ADDITIONAL_FLAGS="${ADDITIONAL_FLAGS:-}"
NODE_ENV_VARS="${NODE_ENV_VARS:-}"
NODE_CPUSET="${NODE_CPUSET:-}"
NODE_MEMORY="${NODE_MEMORY:-}"

[[ "$DOTTRACE" != "true" || "$CLIENT" == "nethermind" ]] || die "dottrace profiling requires CLIENT=nethermind"
[[ "$PERF" != "true" || "$CLIENT" == "nethermind" ]] || die "perf profiling requires CLIENT=nethermind (it needs the runtime perf map)"
case "$DOTNET_TRACE" in
  true|false) ;;
  *) die "DOTNET_TRACE must be true or false (got '$DOTNET_TRACE')" ;;
esac
[[ "$DOTNET_TRACE" != "true" || "$CLIENT" == "nethermind" ]] || die "dotnet-trace requires CLIENT=nethermind (EventPipe is .NET-specific)"
case "$DOTTRACE_MODE" in
  sampling|tracing|timeline) ;;
  *) die "DOTTRACE_MODE must be sampling, tracing, or timeline (got '$DOTTRACE_MODE')" ;;
esac
case "$PROFILE_AFTER_WARMUP" in
  true|false) ;;
  *) die "PROFILE_AFTER_WARMUP must be true or false (got '$PROFILE_AFTER_WARMUP')" ;;
esac
DOTTRACE_DEFERRED="false"
[[ "$DOTTRACE" == "true" && "$PROFILE_AFTER_WARMUP" == "true" ]] && DOTTRACE_DEFERRED="true"

if [[ "$PERF" == "true" ]]; then
  require_perf_access
fi

mkdir -p "$STATE_DIR"
[[ -d "$DB_SOURCE" ]] || {
  log "DB_SOURCE '$DB_SOURCE' is not a directory. Snapshot candidates on this runner:"
  ls -1d /mnt/*/[Nn]ethermind*snapshot* /mnt/*/*/[Nn]ethermind*snapshot* /mnt/*/nethermind-* /mnt/*/geth-* /mnt/*/reth-* 2>/dev/null \
    | sort -u | sed 's/^/  /' || echo "  <none found under /mnt>"
  die "set node_config.db_source to a valid snapshot path"
}
guard_paths

log "=== RPC benchmark node startup ==="
log "Client:     $CLIENT  (instance: $INSTANCE)"
log "Image:      $NODE_IMAGE"
log "Snapshot:   $DB_SOURCE  ($([[ "$DB_ISOLATION" == "direct" ]] && echo "READ-WRITE bind — direct mode" || echo "read-only"))"
log "Isolation:  $DB_ISOLATION"
log "Scratch:    $SCRATCH_ROOT"
log "dotTrace:   $DOTTRACE"
log "perf:       $PERF (${PERF_FREQUENCY}Hz)"
log "dotnet-trace: $DOTNET_TRACE"
[[ "$PROFILE_AFTER_WARMUP" == "true" ]] && log "profilers:  deferred until start-profilers.sh runs after the warm-up"
log "RPC port:   $RPC_PORT  (network: $NETWORK)"
for f in _snapshot_metadata.json _snapshot_web3_clientVersion.json; do
  [[ -f "$DB_SOURCE/$f" ]] && log "  $f: $(tr -d '\n' < "$DB_SOURCE/$f" | head -c 300)"
done

BASELINE_FILE="$STATE_DIR/db-baseline$SUFFIX.txt"
log "Computing DB integrity baseline (tamper tripwire)..."
db_fingerprint "$DB_SOURCE" "$BASELINE_FILE"
log "  baseline: $(wc -l < "$BASELINE_FILE") lines, sha256=$(sha256sum "$BASELINE_FILE" | cut -d' ' -f1)"

ANCHOR_FILE="$SCRATCH_ROOT/fingerprints/$(basename "$DB_SOURCE").txt"
mkdir -p "$(dirname "$ANCHOR_FILE")"
if [[ -f "$ANCHOR_FILE" ]] && [[ "$(head -n 1 "$ANCHOR_FILE")" == "$(head -n 1 "$BASELINE_FILE")" ]] \
    && ! diff -q "$ANCHOR_FILE" "$BASELINE_FILE" >/dev/null 2>&1; then
  log "::warning::Snapshot fingerprint differs from the last verified run's anchor ($ANCHOR_FILE) — an interrupted run may have modified it."
fi

# Only the primary reaps: the reference starts second and must not kill this run's primary.
[[ "$INSTANCE" == "primary" ]] && reap_stale_containers "rpcbench-" "nethermind-rpcbench" "ethcallchaos-bench" "jsonbench-"

RUN_SCRATCH="$SCRATCH_ROOT/run$SUFFIX"
for m in "$RUN_SCRATCH/merged" "$RUN_SCRATCH/ro"; do
  mountpoint -q "$m" 2>/dev/null && { as_root umount "$m" 2>/dev/null || as_root umount -l "$m" 2>/dev/null || true; }
done
assert_no_mounts_under "$RUN_SCRATCH"
as_root rm -rf "$RUN_SCRATCH"
mkdir -p "$RUN_SCRATCH" "$DIAG_DIR"

MOUNT_OPT="rw"
case "$DB_ISOLATION" in
  overlay)
    mkdir -p "$RUN_SCRATCH/upper" "$RUN_SCRATCH/work" "$RUN_SCRATCH/merged"
    log "Mounting overlayfs (lowerdir=read-only source, upperdir=scratch)..."
    as_root mount -t overlay overlay \
      -o "lowerdir=$DB_SOURCE,upperdir=$RUN_SCRATCH/upper,workdir=$RUN_SCRATCH/work,redirect_dir=on,metacopy=on,volatile" "$RUN_SCRATCH/merged" \
      || as_root mount -t overlay overlay -o "lowerdir=$DB_SOURCE,upperdir=$RUN_SCRATCH/upper,workdir=$RUN_SCRATCH/work" "$RUN_SCRATCH/merged" \
      || die "overlay mount failed — pick db_isolation=copy if the runner lacks overlayfs"
    DATA_DIR_SOURCE="$RUN_SCRATCH/merged"
    ;;
  copy)
    log "Copying snapshot to scratch (reflink when supported)..."
    mkdir -p "$RUN_SCRATCH/db"
    cp -a --reflink=auto "$DB_SOURCE/." "$RUN_SCRATCH/db/"
    DATA_DIR_SOURCE="$RUN_SCRATCH/db"
    ;;
  readonly-bind)
    mkdir -p "$RUN_SCRATCH/ro"
    as_root mount --bind "$DB_SOURCE" "$RUN_SCRATCH/ro"
    as_root mount -o remount,ro,bind "$RUN_SCRATCH/ro"
    DATA_DIR_SOURCE="$RUN_SCRATCH/ro"
    MOUNT_OPT="ro"
    ;;
  direct)
    log "::warning::db_isolation=direct — the pristine snapshot is mounted READ-WRITE and the node's startup writes will modify it."
    DATA_DIR_SOURCE="$DB_SOURCE"
    ;;
  *) die "unknown DB_ISOLATION '$DB_ISOLATION' (expected overlay | copy | readonly-bind | direct)" ;;
esac

# geth backups hold the contents of <datadir>/geth, so they mount one level down.
DATA_MOUNT_TARGET="$DATA_DIR_TARGET"
[[ "$CLIENT" == "geth" ]] && DATA_MOUNT_TARGET="$DATA_DIR_TARGET/geth"
log "  datadir view: $DATA_DIR_SOURCE  (mounted $MOUNT_OPT at $DATA_MOUNT_TARGET)"

# Persisted before docker run so stop-node.sh can verify and tear down even if the start fails.
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
  echo "DOTTRACE_DEFERRED=$DOTTRACE_DEFERRED"
  echo "PERF=$PERF"
  echo "PERF_FREQUENCY=$PERF_FREQUENCY"
  echo "DOTNET_TRACE=$DOTNET_TRACE"
  echo "PROFILE_AFTER_WARMUP=$PROFILE_AFTER_WARMUP"
  echo "RPC_PORT=$RPC_PORT"
} > "$STATE_DIR/node$SUFFIX.env"

case "$CLIENT" in
  nethermind)
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
      "--Init.DiscoveryEnabled=false"
      "--Network.MaxActivePeers=0"
      "--Pruning.Mode=None"
      "--HealthChecks.Enabled=false"
      "--Metrics.Enabled=false"
    )
    # shellcheck disable=SC2206
    node_args+=($LAYOUT_FLAGS)
    ;;
  geth)
    [[ "$NETWORK" == "mainnet" ]] || die "CLIENT=geth supports only network=mainnet (got '$NETWORK')"
    node_args=(
      "--datadir=$DATA_DIR_TARGET"
      "--http" "--http.addr=0.0.0.0" "--http.port=8545"
      "--http.api=$GETH_HTTP_API"
      "--http.vhosts=*"
      "--rpc.gascap=$RPC_GAS_CAP"
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
  -p "127.0.0.1:${RPC_PORT}:8545"
  -v "$DATA_DIR_SOURCE:$DATA_MOUNT_TARGET:$MOUNT_OPT"
)
# shellcheck disable=SC2086
for kv in $NODE_ENV_VARS; do docker_args+=(-e "$kv"); done
perf_client_env=()
if [[ "$PERF" == "true" ]]; then
  perf_client_env=(
    DOTNET_PerfMapEnabled=1
    DOTNET_PerfMapShowOptimizationTiers=1
    DOTNET_EnableWriteXorExecute=0
  )
  assert_no_mounts_under "$DIAG_DIR/perf"
  as_root rm -rf "$DIAG_DIR/perf"
  mkdir -p "$DIAG_DIR/perf"
  if [[ "$DOTTRACE" != "true" ]]; then
    for kv in "${perf_client_env[@]}"; do docker_args+=(-e "$kv"); done
  fi
fi
# dotnet-trace (nethermind only): mount the host tool read-only plus an output dir; the collector is
# attached with docker exec by start_profilers, so nothing about the node's launch changes.
if [[ "$DOTNET_TRACE" == "true" ]]; then
  if [[ ! -x "$DOTNET_TRACE_HOST_PATH/dotnet-trace" ]]; then
    log "dotnet-trace not found at $DOTNET_TRACE_HOST_PATH — installing $DOTNET_TRACE_VERSION via dotnet tool..."
    dotnet tool install --version "$DOTNET_TRACE_VERSION" --tool-path "$DOTNET_TRACE_HOST_PATH" dotnet-trace \
      || as_root dotnet tool install --version "$DOTNET_TRACE_VERSION" --tool-path "$DOTNET_TRACE_HOST_PATH" dotnet-trace \
      || die "failed to install dotnet-trace $DOTNET_TRACE_VERSION (is the .NET SDK on the runner?)"
  fi
  assert_no_mounts_under "$DIAG_DIR/dotnet-trace"
  as_root rm -rf "$DIAG_DIR/dotnet-trace"
  mkdir -p "$DIAG_DIR/dotnet-trace"
  docker_args+=(
    -v "$DOTNET_TRACE_HOST_PATH:$DOTNET_TRACE_CONTAINER_PATH:ro"
    -v "$DIAG_DIR/dotnet-trace:$DOTNET_TRACE_OUTPUT_PATH:rw"
  )
fi
[[ -n "$NODE_CPUSET" ]] && docker_args+=(--cpuset-cpus "$NODE_CPUSET")
[[ -n "$NODE_MEMORY" ]] && docker_args+=(--memory "$NODE_MEMORY")

entry_args=()
if [[ "$DOTTRACE" == "true" ]]; then
  if [[ ! -x "$DOTTRACE_HOST_PATH/dottrace" ]]; then
    log "dotTrace CLI not found at $DOTTRACE_HOST_PATH — installing via dotnet tool..."
    dotnet tool install --tool-path "$DOTTRACE_HOST_PATH" JetBrains.dotTrace.GlobalTools \
      || as_root dotnet tool install --tool-path "$DOTTRACE_HOST_PATH" JetBrains.dotTrace.GlobalTools \
      || die "failed to install dotTrace CLI"
  fi
  assert_no_mounts_under "$DIAG_DIR/dottrace"
  as_root rm -rf "$DIAG_DIR/dottrace"
  mkdir -p "$DIAG_DIR/dottrace"
  docker_args+=(-v "$DOTTRACE_HOST_PATH:/opt/dottrace:ro" -v "$DIAG_DIR/dottrace:/dottrace-output:rw" --entrypoint /opt/dottrace/dottrace)
  # Timeline snapshots are .dtt; Reporter.exe's .dtp glob must not pick them up.
  snapshot_ext="$([[ "$DOTTRACE_MODE" == "timeline" ]] && echo dtt || echo dtp)"
  entry_args=(start --framework=NetCore "--profiling-type=${DOTTRACE_MODE^}" "--save-to=/dottrace-output/rpcbench-${NETWORK}${SUFFIX}.${snapshot_ext}" --propagate-exit-code)
  if [[ "$DOTTRACE_DEFERRED" == "true" ]]; then
    # Keep the `start` wrapper (attach cannot do tracing) but hold data collection until
    # start_profilers appends ##dotTrace["start"] to the control file; the launcher must find the
    # file at launch, so it exists (empty) before docker run. SIGINT finalization is unchanged.
    : > "$DIAG_DIR/dottrace/$DOTTRACE_CONTROL_FILE_NAME"
    chmod a+rw "$DIAG_DIR/dottrace/$DOTTRACE_CONTROL_FILE_NAME"
    entry_args+=(--collect-data-from-start=off --service-output=on "--service-input=/dottrace-output/$DOTTRACE_CONTROL_FILE_NAME")
  fi
  entry_args+=(--)
  if [[ "$PERF" == "true" ]]; then
    # The dotTrace launcher is itself .NET; only the client may write a perf map.
    entry_args+=(/usr/bin/env "${perf_client_env[@]}")
  fi
  entry_args+=(/nethermind/nethermind)
fi

docker rm -fv "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Starting $CLIENT container '$CONTAINER_NAME'..."
log "  node args: ${node_args[*]}"
docker run "${docker_args[@]}" "$NODE_IMAGE" ${entry_args[@]+"${entry_args[@]}"} "${node_args[@]}"

wait_for_rpc "http://localhost:${RPC_PORT}" "$HEALTH_TIMEOUT" "$CONTAINER_NAME"
log "=== Node ready for benchmarking ==="

# Start the profilers once the node serves RPC, so they exclude startup. With a warm-up the
# workflow starts them via start-profilers.sh after it, so they exclude the warm-up as well.
if [[ "$PROFILE_AFTER_WARMUP" == "true" ]]; then
  log "profilers deferred: run start-profilers.sh after the warm-up"
elif [[ "$PERF" == "true" || "$DOTNET_TRACE" == "true" ]]; then
  start_profilers "$STATE_DIR/node$SUFFIX.env"
fi
