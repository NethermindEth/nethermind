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
[[ -f "$STATE_DIR/node.env" ]] || die "no node.env in $STATE_DIR (node never started?)"
# shellcheck disable=SC1091
source "$STATE_DIR/node.env"

STOP_GRACE="${STOP_GRACE:-180}"     # seconds; SIGINT (stop-signal) lets dotTrace finalize the .dtp
LOG_OUT="${LOG_OUT:-$STATE_DIR/node.log}"
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
# 3) Verify the pristine snapshot is unchanged (the hard DB-safety guarantee).
# ---------------------------------------------------------------------------
log "Verifying DB snapshot integrity..."
# A fingerprint failure must never look like a clean snapshot: flag it and fall
# through to teardown + the final die (with set -e it would otherwise abort here
# and skip the umount/scratch cleanup below).
if ! db_fingerprint "$DB_SOURCE" "$STATE_DIR/db-final.txt"; then
  log "::error::Failed to compute the final DB fingerprint — snapshot integrity could not be verified."
  integrity_fail=1
elif ! diff -q "$STATE_DIR/db-baseline.txt" "$STATE_DIR/db-final.txt" >/dev/null 2>&1; then
  log "::error::DB SNAPSHOT WAS MODIFIED during the run — this must never happen."
  log "Fingerprint diff (first 60 lines):"
  diff "$STATE_DIR/db-baseline.txt" "$STATE_DIR/db-final.txt" 2>/dev/null | head -n 60 || true
  integrity_fail=1
else
  log "  OK — snapshot unchanged ($(wc -l < "$STATE_DIR/db-final.txt") fingerprint lines match)."
  # Persist the verified fingerprint as the cross-run anchor (compared by the
  # next run's start-node.sh to catch mutations from hard-interrupted runs).
  if [[ -n "${SCRATCH_ROOT:-}" ]]; then
    mkdir -p "$SCRATCH_ROOT/fingerprints" 2>/dev/null || true
    cp "$STATE_DIR/db-final.txt" "$SCRATCH_ROOT/fingerprints/$(basename "$DB_SOURCE").txt" 2>/dev/null || true
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
