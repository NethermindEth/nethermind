#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Stop the benchmark node, collect its full logs (including the shutdown phase),
# collect dotTrace snapshots, VERIFY the pristine DB snapshot is unchanged, and
# tear down the isolated DB view. Exits non-zero if the snapshot was modified.

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

# ---------------------------------------------------------------------------
# 1) Graceful stop FIRST, then capture logs — so the shutdown window (dispose/
#    flush exceptions, dotTrace finalize, the shutdown marker) is scanned too.
# ---------------------------------------------------------------------------
log "Stopping container '$CONTAINER_NAME' (grace ${STOP_GRACE}s for snapshot finalize)..."
docker stop -t "$STOP_GRACE" "$CONTAINER_NAME" >/dev/null 2>&1 || true

log "Capturing node logs -> $LOG_OUT"
docker logs "$CONTAINER_NAME" > "$LOG_OUT" 2>&1 || true

# ---------------------------------------------------------------------------
# 2) Collect dotTrace snapshots (if profiling was enabled).
# ---------------------------------------------------------------------------
if [[ "${DOTTRACE:-}" == "true" ]]; then
  log "dotTrace snapshots under $DIAG_DIR/dottrace:"
  find "$DIAG_DIR/dottrace" -type f 2>/dev/null | sed 's/^/  /' || true
fi

# ---------------------------------------------------------------------------
# 3) Verify the snapshot. Under overlay/copy/readonly-bind it must be unchanged
#    (the hard DB-safety guarantee). Under 'direct' it is intentionally mounted
#    read-write, so changes are expected: record what changed and warn, but do
#    NOT fail the job or update the cross-run anchor.
# ---------------------------------------------------------------------------
if [[ "${DB_ISOLATION:-}" == "direct" ]]; then
  log "direct mode: snapshot was mounted read-write — verifying scope of changes (not a failure)..."
  if ! db_fingerprint "$DB_SOURCE" "$FINAL_FILE"; then
    log "::warning::direct mode: failed to compute the final fingerprint — cannot summarize what changed."
  elif diff -q "$BASELINE_FILE" "$FINAL_FILE" >/dev/null 2>&1; then
    log "  snapshot unchanged despite read-write mount (node made no on-disk changes)."
  else
    changed=$(diff "$BASELINE_FILE" "$FINAL_FILE" 2>/dev/null | grep -cE '^[<>]' || true)
    log "::warning::direct mode: snapshot changed as expected (${changed} differing fingerprint lines). First 40:"
    diff "$BASELINE_FILE" "$FINAL_FILE" 2>/dev/null | grep -E '^[<>]' | head -n 40 || true
  fi
elif ! db_fingerprint "$DB_SOURCE" "$FINAL_FILE"; then
  # A fingerprint failure must never look like a clean snapshot: flag it and fall
  # through to teardown + the final die (with set -e it would otherwise abort here
  # and skip the umount/scratch cleanup below).
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

# ---------------------------------------------------------------------------
# 4) Tear down the isolated view. Never touches DB_SOURCE.
# ---------------------------------------------------------------------------
docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
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
log "=== Node stopped; snapshot verified pristine ==="
