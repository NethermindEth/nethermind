#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Stop the node, capture its log, verify the snapshot is unchanged (warn-only under direct), tear down the DB view.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${STATE_DIR:?directory where start-node.sh persisted state}"
NODE_ENV_FILE="${NODE_ENV_FILE:-$STATE_DIR/node.env}"
[[ -f "$NODE_ENV_FILE" ]] || die "no $(basename "$NODE_ENV_FILE") in $STATE_DIR (node never started?)"
# shellcheck disable=SC1090,SC1091
source "$NODE_ENV_FILE"

SUFFIX="${INSTANCE_SUFFIX:-}"
BASELINE_FILE="$STATE_DIR/db-baseline$SUFFIX.txt"
FINAL_FILE="$STATE_DIR/db-final$SUFFIX.txt"
STOP_GRACE="${STOP_GRACE:-180}"
LOG_OUT="${LOG_OUT:-$STATE_DIR/node$SUFFIX.log}"
integrity_fail=0

log "Stopping container '$CONTAINER_NAME' (grace ${STOP_GRACE}s)..."
docker stop -t "$STOP_GRACE" "$CONTAINER_NAME" >/dev/null 2>&1 || true
log "Capturing node logs -> $LOG_OUT"
docker logs "$CONTAINER_NAME" > "$LOG_OUT" 2>&1 || true

if [[ "${DOTTRACE:-}" == "true" ]]; then
  log "dotTrace snapshots under $DIAG_DIR/dottrace:"
  find "$DIAG_DIR/dottrace" -type f 2>/dev/null | sed 's/^/  /' || true
fi

if [[ "${DB_ISOLATION:-}" == "direct" ]]; then
  if ! (db_fingerprint "$DB_SOURCE" "$FINAL_FILE"); then
    log "::warning::direct mode: failed to compute the final fingerprint"
  elif diff -q "$BASELINE_FILE" "$FINAL_FILE" >/dev/null 2>&1; then
    log "  snapshot unchanged despite read-write mount."
  else
    log "::warning::direct mode: snapshot changed as expected ($(diff "$BASELINE_FILE" "$FINAL_FILE" 2>/dev/null | grep -cE '^[<>]' || true) differing fingerprint lines). First 40:"
    diff "$BASELINE_FILE" "$FINAL_FILE" 2>/dev/null | grep -E '^[<>]' | head -n 40 || true
  fi
elif ! (db_fingerprint "$DB_SOURCE" "$FINAL_FILE"); then
  log "::error::Failed to compute the final DB fingerprint — snapshot integrity could not be verified."
  integrity_fail=1
elif ! diff -q "$BASELINE_FILE" "$FINAL_FILE" >/dev/null 2>&1; then
  log "::error::DB SNAPSHOT WAS MODIFIED during the run — this must never happen. Fingerprint diff (first 60 lines):"
  diff "$BASELINE_FILE" "$FINAL_FILE" 2>/dev/null | head -n 60 || true
  integrity_fail=1
else
  log "  OK — snapshot unchanged ($(wc -l < "$FINAL_FILE") fingerprint lines match)."
  if [[ -n "${SCRATCH_ROOT:-}" ]]; then
    mkdir -p "$SCRATCH_ROOT/fingerprints" 2>/dev/null || true
    cp "$FINAL_FILE" "$SCRATCH_ROOT/fingerprints/$(basename "$DB_SOURCE").txt" 2>/dev/null || true
  fi
fi

docker rm -fv "$CONTAINER_NAME" >/dev/null 2>&1 || true
case "${DB_ISOLATION:-}" in
  overlay)       m="$RUN_SCRATCH/merged" ;;
  readonly-bind) m="$RUN_SCRATCH/ro" ;;
  *)             m="" ;;
esac
[[ -n "$m" ]] && { as_root umount "$m" 2>/dev/null || as_root umount -l "$m" 2>/dev/null || true; }
assert_no_mounts_under "$RUN_SCRATCH"
as_root rm -rf "$RUN_SCRATCH" 2>/dev/null || true
log "  scratch removed."

[[ "$integrity_fail" == "0" ]] || die "DB integrity check FAILED — snapshot verification did not pass."
log "=== Node stopped; snapshot verified pristine ==="
