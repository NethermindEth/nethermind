#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Defensive cleanup for the RPC benchmark workflow: removes benchmark containers
# from ANY run (stale ones included), unmounts everything under the scratch
# root, and wipes only the scratch subtrees — with the same canonical-path
# guards as start-node.sh, so a typo'd scratch_root/db_source can never turn
# this into an rm -rf of the snapshot (or worse). Best-effort: never fails the
# job, but refuses to delete anything it cannot prove is safe.

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${SCRATCH_ROOT:?scratch root to clean}"
DB_SOURCE="${DB_SOURCE:-}"

reap_stale_containers "nethermind-rpcbench" "ethcallchaos-bench"

SCRATCH_ROOT="$(realpath -m -- "$SCRATCH_ROOT")" || { log "cannot canonicalize SCRATCH_ROOT — skipping scratch wipe"; exit 0; }
assert_sane_dir "$SCRATCH_ROOT" "SCRATCH_ROOT"
if [[ -n "$DB_SOURCE" && -d "$DB_SOURCE" ]]; then
  DB_SOURCE="$(realpath -e -- "$DB_SOURCE")" || DB_SOURCE=""
  if [[ -n "$DB_SOURCE" ]]; then
    case "$SCRATCH_ROOT/" in
      "$DB_SOURCE"/*) die "SCRATCH_ROOT resolves inside DB_SOURCE — refusing to clean" ;;
    esac
    case "$DB_SOURCE/" in
      "$SCRATCH_ROOT"/*) die "DB_SOURCE resolves inside SCRATCH_ROOT — refusing to clean" ;;
    esac
  fi
fi

# Unmount anything still mounted under scratch (overlay merged, ro binds, ...).
while IFS= read -r m; do
  log "Unmounting leftover mount: $m"
  as_root umount "$m" 2>/dev/null || as_root umount -l "$m" 2>/dev/null || true
done < <(awk -v d="$SCRATCH_ROOT" '$2 == d || index($2, d "/") == 1 { print $2 }' /proc/self/mounts 2>/dev/null | sort -r)

for sub in run diag ethcallchaos; do
  target="$SCRATCH_ROOT/$sub"
  [[ -e "$target" ]] || continue
  if ! (assert_no_mounts_under "$target") 2>/dev/null; then
    log "::warning::'$target' still has live mounts — skipping its deletion"
    continue
  fi
  as_root rm -rf "$target" 2>/dev/null || true
done
log "Defensive cleanup done."
