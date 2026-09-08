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

: "${DB_SOURCE:?path to the pristine client datadir snapshot (e.g. /data/nethermind/nethermind-flat-25490000)}"
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
# sampling | tracing | timeline. Timeline snapshots are UI-only (Reporter cannot emit XML);
# line-by-line is not offered because the client images carry no PDBs.
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
NODE_ENV_VARS="${NODE_ENV_VARS:-}"                 # extra docker -e assignments, e.g. "DOTNET_TieredCompilation=0"
NODE_CPUSET="${NODE_CPUSET:-}"                     # e.g. 2-7,10-15 (expb pins the client to these cores)
NODE_MEMORY="${NODE_MEMORY:-}"                     # e.g. 64g
ACCOUNT_INDEX_SWEEP="${ACCOUNT_INDEX_SWEEP:-false}"
ACCOUNT_INDEX_PREPARE_MODE="${ACCOUNT_INDEX_PREPARE_MODE:-}"
ACCOUNT_INDEX_PREPARE_THRESHOLD="${ACCOUNT_INDEX_PREPARE_THRESHOLD:-}"
ACCOUNT_INDEX_HELPER_PATH="${ACCOUNT_INDEX_HELPER_PATH:-}"
ACCOUNT_INDEX_PREPARE_OUTPUT="${ACCOUNT_INDEX_PREPARE_OUTPUT:-$STATE_DIR/account-index-prepare.json}"

if [[ "$DOTTRACE" == "true" && "$CLIENT" != "nethermind" ]]; then
  die "dottrace profiling requires CLIENT=nethermind (dotTrace is .NET-specific)"
fi
if [[ "$PERF" == "true" && "$CLIENT" != "nethermind" ]]; then
  die "perf profiling is wired for CLIENT=nethermind (it needs the runtime perf map)"
fi
case "$DOTNET_TRACE" in
  true|false) ;;
  *) die "DOTNET_TRACE must be true or false (got '$DOTNET_TRACE')" ;;
esac
if [[ "$DOTNET_TRACE" == "true" && "$CLIENT" != "nethermind" ]]; then
  die "dotnet-trace requires CLIENT=nethermind (EventPipe is .NET-specific)"
fi
case "$ACCOUNT_INDEX_SWEEP" in
  true|false) ;;
  *) die "ACCOUNT_INDEX_SWEEP must be true or false (got '$ACCOUNT_INDEX_SWEEP')" ;;
esac
if [[ "$ACCOUNT_INDEX_SWEEP" == "true" ]]; then
  [[ "$CLIENT" == "nethermind" ]] || die "Account index sweep requires CLIENT=nethermind"
  [[ "$DB_ISOLATION" == "overlay" ]] || die "Account index sweep requires DB_ISOLATION=overlay"
  [[ -x "$ACCOUNT_INDEX_HELPER_PATH" ]] || die "Account index helper is missing or not executable: $ACCOUNT_INDEX_HELPER_PATH"
  case "$ACCOUNT_INDEX_PREPARE_MODE" in
    binary|interpolation)
      [[ "$ACCOUNT_INDEX_PREPARE_THRESHOLD" == "-1" ]] \
        || die "${ACCOUNT_INDEX_PREPARE_MODE} preparation requires threshold=-1"
      ;;
    auto)
      if [[ ! "$ACCOUNT_INDEX_PREPARE_THRESHOLD" =~ ^(0|0\.[0-9]+|1(\.0+)?)$ ]]; then
        die "auto preparation threshold must be a finite value in [0,1]"
      fi
      ;;
    *) die "unknown Account index preparation mode '$ACCOUNT_INDEX_PREPARE_MODE'" ;;
  esac
  [[ -z "$ADDITIONAL_FLAGS" ]] \
    || die "ACCOUNT_INDEX_SWEEP does not accept generic ADDITIONAL_FLAGS"
  [[ -z "$NODE_ENV_VARS" ]] \
    || die "ACCOUNT_INDEX_SWEEP does not accept generic NODE_ENV_VARS"
  if [[ "${ARCH:-}" == "arm64" ]]; then
    ARCH=arm64 SCRATCH_ROOT="$SCRATCH_ROOT" "$HERE/check-storage-space.sh" pre-account-index
  fi
fi
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
log "perf:       $PERF (${PERF_FREQUENCY}Hz)"
log "dotnet-trace: $DOTNET_TRACE"
[[ "$PROFILE_AFTER_WARMUP" == "true" ]] && log "profilers:  deferred until start-profilers.sh runs after the warm-up"
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
  reap_stale_containers "rpcbench-" "nethermind-rpcbench" "ethcallchaos-bench" "jsonbench-" "account-index-prepare-"
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
  echo "DOTTRACE_DEFERRED=$DOTTRACE_DEFERRED"
  echo "PERF=$PERF"
  echo "PERF_FREQUENCY=$PERF_FREQUENCY"
  echo "DOTNET_TRACE=$DOTNET_TRACE"
  echo "PROFILE_AFTER_WARMUP=$PROFILE_AFTER_WARMUP"
  echo "RPC_PORT=$RPC_PORT"
  echo "ACCOUNT_INDEX_SWEEP=$ACCOUNT_INDEX_SWEEP"
  echo "ACCOUNT_INDEX_PREPARE_MODE=$ACCOUNT_INDEX_PREPARE_MODE"
  echo "ACCOUNT_INDEX_PREPARE_THRESHOLD=$ACCOUNT_INDEX_PREPARE_THRESHOLD"
  echo "ACCOUNT_INDEX_PREPARE_OUTPUT=$ACCOUNT_INDEX_PREPARE_OUTPUT"
} > "$STATE_DIR/node$SUFFIX.env"

cleanup_failed_start() {
  local status="${1:-$?}"
  if [[ "$status" -ne 0 && "$ACCOUNT_INDEX_SWEEP" == "true" && -f "$STATE_DIR/node$SUFFIX.env" ]]; then
    log "Account preparation/start failed; invoking stop-node.sh for fingerprint verification and isolated-view teardown"
    if STATE_DIR="$STATE_DIR" NODE_ENV_FILE="$STATE_DIR/node$SUFFIX.env" LOG_OUT="$STATE_DIR/node$SUFFIX.log" \
        "$HERE/stop-node.sh"; then
      : > "$STATE_DIR/account-index-start-cleaned"
    else
      log "ERROR: stop-node.sh failed while cleaning up the failed Account preparation"
    fi
  fi
  exit "$status"
}
trap cleanup_failed_start EXIT

account_index_node_option() {
  local search_type
  case "$ACCOUNT_INDEX_PREPARE_MODE" in
    binary) search_type="kBinary" ;;
    interpolation) search_type="kInterpolation" ;;
    auto) search_type="kAuto" ;;
    *) die "cannot construct Account index option for mode '$ACCOUNT_INDEX_PREPARE_MODE'" ;;
  esac
  printf '%s\n' "--Db.FlatAccountDbAdditionalRocksDbOptions=block_based_table_factory.index_block_search_type=${search_type};block_based_table_factory.uniform_cv_threshold=${ACCOUNT_INDEX_PREPARE_THRESHOLD};"
}

prepare_account_index() {
  [[ "$ACCOUNT_INDEX_SWEEP" == "true" ]] || return 0
  local account_db="$DATA_DIR_SOURCE/mainnet/flat"
  local result_dir="$STATE_DIR/account-index-prepare"
  local helper_dir prep_container prep_status helper_output
  local -a prep_docker_args
  helper_dir="$(dirname -- "$ACCOUNT_INDEX_HELPER_PATH")"
  prep_container="${CONTAINER_NAME}-account-index-prepare"
  helper_output="$result_dir/helper.json"

  [[ -d "$account_db" ]] || die "Account index preparation path '$account_db' is missing; refusing to guess the snapshot layout"
  [[ -f "$account_db/CURRENT" ]] || die "Account index preparation path '$account_db/CURRENT' is missing; refusing to guess the snapshot layout"
  mkdir -p "$result_dir"
  rm -f "$helper_output" "$ACCOUNT_INDEX_PREPARE_OUTPUT"
  # The marker belongs to the mounted view root (/work in the helper), never to the canonical
  # snapshot. It lets the helper prove that it owns this isolated view before rewriting it.
  printf 'createdbyharness\n' > "$DATA_DIR_SOURCE/.account-index-prepare-owned"
  [[ -f "$DATA_DIR_SOURCE/.account-index-prepare-owned" ]] \
    || die "could not create .account-index-prepare-owned inside the isolated Account view"

  cleanup_prepare() {
    docker rm -fv "$prep_container" >/dev/null 2>&1 || true
  }
  trap 'status=$?; cleanup_prepare; cleanup_failed_start "$status"' EXIT

  log "Preparing isolated Account view: mode=$ACCOUNT_INDEX_PREPARE_MODE threshold=$ACCOUNT_INDEX_PREPARE_THRESHOLD"
  docker rm -fv "$prep_container" >/dev/null 2>&1 || true
  prep_docker_args=(
    run -d --name "$prep_container" --restart no
    --entrypoint /opt/account-index-prepare/AccountIndexPrepare
    -v "$DATA_DIR_SOURCE:/work:rw"
    -v "$helper_dir:/opt/account-index-prepare:ro"
    -v "$result_dir:/work/result:rw"
  )
  [[ -n "$NODE_CPUSET" ]] && prep_docker_args+=(--cpuset-cpus "$NODE_CPUSET")
  [[ -n "$NODE_MEMORY" ]] && prep_docker_args+=(--memory "$NODE_MEMORY")
  prep_docker_args+=(
    "$NODE_IMAGE"
    --db-path /work/mainnet/flat
    --scratch-root /work
    --mode "$ACCOUNT_INDEX_PREPARE_MODE"
    --threshold "$ACCOUNT_INDEX_PREPARE_THRESHOLD"
    --output /work/result/helper.json
  )
  docker "${prep_docker_args[@]}" >/dev/null \
    || die "could not start Account index preparation container"

  prep_status="$(docker wait "$prep_container" 2>/dev/null || echo 125)"
  docker logs "$prep_container" > "$result_dir/helper.log" 2>&1 || true
  if [[ "$prep_status" != "0" ]]; then
    die "Account index preparation failed (helper exit ${prep_status}); aggregate diagnostics were retained in runner scratch"
  fi
  [[ -s "$helper_output" ]] || die "Account index helper produced no aggregate result"
  helper_sha256="$(sha256sum "$ACCOUNT_INDEX_HELPER_PATH" | awk '{print $1}')"
  python3 "$HERE/account_index_sweep.py" validate-result "$helper_output" "$ACCOUNT_INDEX_PREPARE_OUTPUT" \
    --mode "$ACCOUNT_INDEX_PREPARE_MODE" --threshold "$ACCOUNT_INDEX_PREPARE_THRESHOLD" \
    --helper-sha256 "$helper_sha256" \
    || die "Account index helper result failed the fixed aggregate schema"
  [[ -f "$DATA_DIR_SOURCE/.account-index-prepare-owned" ]] \
    || die "Account index ownership marker disappeared from the isolated view"
  log "Account preparation aggregate validated: $(basename "$ACCOUNT_INDEX_PREPARE_OUTPUT")"
  cleanup_prepare
  trap - EXIT
  trap cleanup_failed_start EXIT
}

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
      # No background pruning while serving a parked snapshot.
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
if [[ "$ACCOUNT_INDEX_SWEEP" == "true" ]]; then
  node_args+=("$(account_index_node_option)")
fi

# The helper rewrites the same overlay view that the node will open. It must complete before the
# node container is created, otherwise the node can open stale Account SSTs while the rewrite runs.
prepare_account_index

docker_args=(
  -d --name "$CONTAINER_NAME"
  --restart no
  --stop-signal SIGINT
  # Loopback-only: the load generators run on this host; publishing on all
  # interfaces would let other network hosts hit the node mid-benchmark.
  -p "127.0.0.1:${RPC_PORT}:8545"
  -v "$DATA_DIR_SOURCE:$DATA_MOUNT_TARGET:$MOUNT_OPT"
)
# Production-default code generation (no DOTNET_* pins); one-off experiments use NODE_ENV_VARS.
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

# dotTrace (nethermind only): mount the host CLI and wrap the node binary, as expb's
# --dottrace does. SIGINT (stop-signal) lets dotTrace finalize the snapshot.
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
  # Timeline snapshots carry dotTrace's .dtt extension; keeping .dtp for them would let
  # Reporter.exe's .dtp glob pick up a snapshot it cannot convert.
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
# Nethermind keeps the image's entrypoint.sh (as expb and production do): it applies
# host tuning and enables a shipped PGO profile, which a direct binary call skips.
# geth/reth official images already have the client binary as their entrypoint;
# node_args are passed as the container command.

# 4) Start the node.
docker rm -fv "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Starting $CLIENT container '$CONTAINER_NAME'..."
log "  node args: ${node_args[*]}"
# ${arr[@]+...} keeps the empty-array expansion safe under set -u on bash < 4.4.
docker run "${docker_args[@]}" "$NODE_IMAGE" ${entry_args[@]+"${entry_args[@]}"} "${node_args[@]}"

# 5) Wait for the node to serve JSON-RPC.
wait_for_rpc "http://localhost:${RPC_PORT}" "$HEALTH_TIMEOUT" "$CONTAINER_NAME"
log "=== Node ready for benchmarking ==="

# 6) Start the profilers once the node serves RPC, so they exclude startup. With a warm-up the
#    workflow starts them via start-profilers.sh after it, so they exclude the warm-up as well.
if [[ "$PROFILE_AFTER_WARMUP" == "true" ]]; then
  log "profilers deferred: run start-profilers.sh after the warm-up"
elif [[ "$PERF" == "true" || "$DOTNET_TRACE" == "true" ]]; then
  start_profilers "$STATE_DIR/node$SUFFIX.env"
fi
