#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Stop the benchmark node, collect dotTrace snapshots, VERIFY the pristine DB
# backup is byte-for-byte unchanged, and tear down the isolated DB view.
# Exits non-zero if the backup was modified in any way.

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${STATE_DIR:?directory where start-node.sh persisted state}"
[[ -f "$STATE_DIR/node.env" ]] || die "no node.env in $STATE_DIR (node never started?)"
# shellcheck disable=SC1091
source "$STATE_DIR/node.env"

STOP_GRACE="${STOP_GRACE:-180}"     # seconds; SIGINT (image STOPSIGNAL) lets dotTrace finalize the .dtp
LOG_OUT="${LOG_OUT:-$STATE_DIR/node.log}"
integrity_fail=0

# ---------------------------------------------------------------------------
# 1) Capture node logs (scanned later for Exception / Invalid Block).
# ---------------------------------------------------------------------------
log "Capturing node logs -> $LOG_OUT"
docker logs "$CONTAINER_NAME" > "$LOG_OUT" 2>&1 || true

# ---------------------------------------------------------------------------
# 2) Graceful stop. The diag image's STOPSIGNAL is SIGINT, which lets
#    'dottrace start' write out the snapshot before the process exits.
# ---------------------------------------------------------------------------
log "Stopping container '$CONTAINER_NAME' (grace ${STOP_GRACE}s for snapshot finalize)..."
docker stop -t "$STOP_GRACE" "$CONTAINER_NAME" >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# 3) Collect dotTrace snapshots (if profiling was enabled).
# ---------------------------------------------------------------------------
if [[ "${DOTTRACE:-}" == "true" ]]; then
  log "dotTrace snapshots under $DIAG_DIR/dottrace:"
  find "$DIAG_DIR/dottrace" -type f 2>/dev/null | sed 's/^/  /' || true
fi

# ---------------------------------------------------------------------------
# 4) Verify the pristine backup is unchanged (the hard DB-safety guarantee).
# ---------------------------------------------------------------------------
log "Verifying DB backup integrity..."
db_fingerprint "$DB_SOURCE" "$STATE_DIR/db-final.txt"
if ! diff -q "$STATE_DIR/db-baseline.txt" "$STATE_DIR/db-final.txt" >/dev/null 2>&1; then
  log "::error::DB BACKUP WAS MODIFIED during the run — this must never happen."
  log "Fingerprint diff (first 60 lines):"
  diff "$STATE_DIR/db-baseline.txt" "$STATE_DIR/db-final.txt" 2>/dev/null | head -n 60 || true
  integrity_fail=1
else
  log "  OK — backup unchanged ($(wc -l < "$STATE_DIR/db-final.txt") fingerprint lines match)."
fi

# ---------------------------------------------------------------------------
# 5) Tear down the isolated view. Never touches DB_SOURCE.
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
as_root rm -rf "$RUN_SCRATCH" 2>/dev/null || true
log "  scratch removed."

if [[ "$integrity_fail" == "1" ]]; then
  die "DB integrity check FAILED — backup was modified (see diff above)."
fi
log "=== Node stopped; backup verified pristine ==="
