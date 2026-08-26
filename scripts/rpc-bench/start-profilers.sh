#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Start the profilers (perf, deferred dotTrace collection) against a node started with
# PROFILE_AFTER_WARMUP=true, once the warm-up load has run: the profiles then cover only
# the measured phase instead of startup, snapshot persistence and JIT tiering.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${STATE_DIR:?directory where start-node.sh persisted state}"
# NODE_ENV_FILE selects the instance (node.env = primary, node-reference.env = reference).
NODE_ENV_FILE="${NODE_ENV_FILE:-$STATE_DIR/node.env}"
[[ -f "$NODE_ENV_FILE" ]] || die "no $(basename "$NODE_ENV_FILE") in $STATE_DIR (node never started?)"
# shellcheck disable=SC1090,SC1091
source "$NODE_ENV_FILE"

if [[ "${PROFILE_AFTER_WARMUP:-false}" != "true" ]]; then
  die "node $CONTAINER_NAME was started without PROFILE_AFTER_WARMUP=true — its profilers already run since RPC became ready"
fi
if [[ "${PERF:-false}" == "true" ]]; then
  require_perf_access
fi
docker ps --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"   || die "container '$CONTAINER_NAME' is not running — nothing to profile"

log "=== Starting profilers after the warm-up (perf: ${PERF:-false}, dotTrace: ${DOTTRACE:-false}) ==="
start_profilers "$NODE_ENV_FILE"
log "=== Profilers running; the measured phase may begin ==="
