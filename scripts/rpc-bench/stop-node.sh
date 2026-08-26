#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Stop the benchmark node, collect full logs (incl. shutdown) and dotTrace snapshots,
# verify the pristine DB snapshot is unchanged, and tear down the isolated DB view.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${STATE_DIR:?directory where start-node.sh persisted state}"
# NODE_ENV_FILE selects which instance to stop (node.env = primary,
# node-reference.env = the comparison reference node).
NODE_ENV_FILE="${NODE_ENV_FILE:-$STATE_DIR/node.env}"
[[ -f "$NODE_ENV_FILE" ]] || die "no $(basename "$NODE_ENV_FILE") in $STATE_DIR (node never started?)"
# shellcheck disable=SC1090,SC1091
source "$NODE_ENV_FILE"

SUFFIX="${INSTANCE_SUFFIX:-}"
BASELINE_FILE="$STATE_DIR/db-baseline$SUFFIX.txt"
FINAL_FILE="$STATE_DIR/db-final$SUFFIX.txt"
STOP_GRACE="${STOP_GRACE:-180}"     # seconds; SIGINT (stop-signal) lets dotTrace finalize the .dtp
LOG_OUT="${LOG_OUT:-$STATE_DIR/node$SUFFIX.log}"
integrity_fail=0
perf_fail=0
dotnet_trace_fail=0

# Stop the dotnet-trace collector first, while the container is still up: SIGINT delivered inside
# the container is what finalizes the .nettrace, and the exec dies with the container.
if [[ "${DOTNET_TRACE:-false}" == "true" ]]; then
  nettrace="$DIAG_DIR/dotnet-trace/rpcbench$SUFFIX.nettrace"
  if [[ -z "${DOTNET_TRACE_PID:-}" || -z "${DOTNET_TRACE_COLLECTOR_PID:-}" ]]; then
    log "ERROR: dotnet-trace was requested but its collector was never attached (no PID persisted)"
    dotnet_trace_fail=1
  else
    log "Stopping dotnet-trace (container pid $DOTNET_TRACE_COLLECTOR_PID)..."
    stop_dotnet_trace_collector "$CONTAINER_NAME" "$DOTNET_TRACE_PID" "$DOTNET_TRACE_COLLECTOR_PID" \
      || dotnet_trace_fail=1
  fi
  if [[ -s "$nettrace" ]]; then
    log "dotnet-trace: $(du -h "$nettrace" | cut -f1) $nettrace"
  else
    log "ERROR: dotnet-trace produced no $nettrace"
    dotnet_trace_fail=1
  fi
  if [[ "$dotnet_trace_fail" == "1" ]]; then
    sed 's/^/    /' "$DIAG_DIR/dotnet-trace/dotnet-trace-collect$SUFFIX.log" 2>/dev/null | tail -n 20 || true
  fi
fi

# 1) Graceful stop FIRST, then capture logs — so the shutdown window (dispose/flush
#    exceptions, dotTrace finalize, shutdown marker) is scanned too.
# 0) Finalize perf while the container is still up: symbolization reads the perf
#    map and the mapped shared objects through /proc/<pid>/root, which disappears
#    with the container.
if [[ "${PERF:-false}" == "true" ]]; then
  if [[ -z "${PERF_PID:-}" || -z "${PERF_NODE_PID:-}" || -z "${PERF_CONTAINER_PID:-}" \
      || -z "${PERF_RECORDER_START_TIME:-}" || -z "${PERF_RECORDER_COMM:-}" \
      || -z "${PERF_RECORDER_EXE:-}" ]]; then
    log "ERROR: perf was requested but its recorder or client PID was not persisted"
    perf_fail=1
  else
    log "Stopping perf (pid $PERF_PID) and folding the profile..."
    if ! signal_perf_recorder_if_matches INT "$PERF_PID" "$PERF_RECORDER_START_TIME" \
        "$PERF_RECORDER_COMM" "$PERF_RECORDER_EXE"; then
      log "ERROR: perf recorder changed identity or exited; refusing to signal pid $PERF_PID"
      perf_fail=1
    else
      for _ in $(seq 1 120); do
        kill -0 "$PERF_PID" 2>/dev/null || break
        if ! perf_recorder_matches "$PERF_PID" "$PERF_RECORDER_START_TIME" \
            "$PERF_RECORDER_COMM" "$PERF_RECORDER_EXE"; then
          log "ERROR: perf recorder changed identity while stopping; refusing further signals"
          perf_fail=1
          break
        fi
        sleep 1
      done
      if [[ "$perf_fail" == "0" ]] && kill -0 "$PERF_PID" 2>/dev/null; then
        if ! signal_perf_recorder_if_matches KILL "$PERF_PID" "$PERF_RECORDER_START_TIME" \
            "$PERF_RECORDER_COMM" "$PERF_RECORDER_EXE"; then
          log "ERROR: perf recorder changed identity before SIGKILL; refusing to signal pid $PERF_PID"
        else
          log "ERROR: perf did not stop cleanly"
        fi
        perf_fail=1
      fi
    fi
    wait "$PERF_PID" 2>/dev/null || true

    perf_data="$DIAG_DIR/perf/perf$SUFFIX.data"
    folded_profile="$DIAG_DIR/perf/perf$SUFFIX.folded"
    if [[ ! -s "$perf_data" ]]; then
      log "ERROR: perf recorded no data"
      perf_fail=1
    else
      source_map="/tmp/perf-${PERF_CONTAINER_PID}.map"
      target_map="/tmp/perf-${PERF_NODE_PID}.map"
      if [[ "$PERF_CONTAINER_PID" == "$PERF_NODE_PID" ]]; then
        map_command='test -s "$1"'
        map_arguments=(sh "$source_map")
      else
        map_command='test -s "$1" && cp "$1" "$2"'
        map_arguments=(sh "$source_map" "$target_map")
      fi
      # The runtime's map is keyed by its container PID. Copy only that map to the
      # host PID name perf expects; concatenating every map would mix dotTrace's PID space.
      if ! docker exec "$CONTAINER_NAME" sh -c "$map_command" "${map_arguments[@]}"; then
        log "ERROR: could not prepare the Nethermind perf map from $source_map"
        perf_fail=1
      fi

      folded_tmp="${folded_profile}.tmp"
      rm -f "$folded_tmp"
      if [[ "$perf_fail" == "0" ]] \
          && perf script --symfs "/proc/$PERF_NODE_PID/root" --input "$perf_data" \
             | awk -f "$HERE/../perf-fold.awk" > "$folded_tmp" \
          && bash "$HERE/../validate-folded-profile.sh" "$folded_tmp"; then
        mv "$folded_tmp" "$folded_profile"
        log "perf profile folded: $(wc -l < "$folded_profile") stacks"
      else
        rm -f "$folded_tmp"
        log "ERROR: perf folding failed or produced an empty perf.folded"
        perf_fail=1
      fi
    fi
  fi
fi

log "Stopping container '$CONTAINER_NAME' (grace ${STOP_GRACE}s for snapshot finalize)..."
docker stop -t "$STOP_GRACE" "$CONTAINER_NAME" >/dev/null 2>&1 || true

log "Capturing node logs -> $LOG_OUT"
docker logs "$CONTAINER_NAME" > "$LOG_OUT" 2>&1 || true

# 2) Collect dotTrace snapshots (if profiling was enabled).
if [[ "${DOTTRACE:-}" == "true" ]]; then
  log "dotTrace snapshots under $DIAG_DIR/dottrace:"
  find "$DIAG_DIR/dottrace" -type f 2>/dev/null | sed 's/^/  /' || true
fi

# 3) Verify the snapshot: under overlay/copy/readonly-bind it MUST be unchanged
#    (DB-safety); under 'direct' it's read-write so changes are expected — warn only.
if [[ "${DB_ISOLATION:-}" == "direct" ]]; then
  log "direct mode: snapshot was mounted read-write — verifying scope of changes (not a failure)..."
  # Subshell: db_fingerprint die()s on failure, which would abort teardown without one.
  if ! (db_fingerprint "$DB_SOURCE" "$FINAL_FILE"); then
    log "::warning::direct mode: failed to compute the final fingerprint — cannot summarize what changed."
  elif diff -q "$BASELINE_FILE" "$FINAL_FILE" >/dev/null 2>&1; then
    log "  snapshot unchanged despite read-write mount (node made no on-disk changes)."
  else
    changed=$(diff "$BASELINE_FILE" "$FINAL_FILE" 2>/dev/null | grep -cE '^[<>]' || true)
    log "::warning::direct mode: snapshot changed as expected (${changed} differing fingerprint lines). First 40:"
    diff "$BASELINE_FILE" "$FINAL_FILE" 2>/dev/null | grep -E '^[<>]' | head -n 40 || true
  fi
elif ! (db_fingerprint "$DB_SOURCE" "$FINAL_FILE"); then
  # A fingerprint failure must never look like a clean snapshot: flag it and fall
  # through to teardown + final die (set -e would else skip umount/scratch cleanup).
  log "::error::Failed to compute the final DB fingerprint — snapshot integrity could not be verified."
  integrity_fail=1
elif ! diff -q "$BASELINE_FILE" "$FINAL_FILE" >/dev/null 2>&1; then
  log "::error::DB SNAPSHOT WAS MODIFIED during the run — this must never happen."
  log "Fingerprint diff (first 60 lines):"
  diff "$BASELINE_FILE" "$FINAL_FILE" 2>/dev/null | head -n 60 || true
  integrity_fail=1
else
  log "  OK — snapshot unchanged ($(wc -l < "$FINAL_FILE") fingerprint lines match)."
  # Persist the verified fingerprint as the cross-run anchor (compared by the
  # next run's start-node.sh to catch mutations from hard-interrupted runs).
  if [[ -n "${SCRATCH_ROOT:-}" ]]; then
    mkdir -p "$SCRATCH_ROOT/fingerprints" 2>/dev/null || true
    cp "$FINAL_FILE" "$SCRATCH_ROOT/fingerprints/$(basename "$DB_SOURCE").txt" 2>/dev/null || true
  fi
fi

# 4) Tear down the isolated view. Never touches DB_SOURCE.
docker rm -fv "$CONTAINER_NAME" >/dev/null 2>&1 || true
case "${DB_ISOLATION:-}" in
  overlay)
    as_root umount "$RUN_SCRATCH/merged" 2>/dev/null \
      || as_root umount -l "$RUN_SCRATCH/merged" 2>/dev/null || true
    ;;
  readonly-bind)
    as_root umount "$RUN_SCRATCH/ro" 2>/dev/null \
      || as_root umount -l "$RUN_SCRATCH/ro" 2>/dev/null || true
    ;;
esac
# Never rm -rf through a still-live mount (e.g. a failed umount of the
# read-only bind of the snapshot) — fail loudly instead.
assert_no_mounts_under "$RUN_SCRATCH"
as_root rm -rf "$RUN_SCRATCH" 2>/dev/null || true
log "  scratch removed."

if [[ "$integrity_fail" == "1" ]]; then
  die "DB integrity check FAILED — snapshot verification did not pass (see errors above)."
fi
if [[ "$perf_fail" == "1" ]]; then
  die "perf profiling FAILED — no non-empty perf.folded was produced (see errors above)."
fi
if [[ "$dotnet_trace_fail" == "1" ]]; then
  die "dotnet-trace collection FAILED — no finalized .nettrace was produced (see errors above)."
fi
log "=== Node stopped; snapshot verified pristine ==="
