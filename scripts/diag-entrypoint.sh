#!/bin/bash
# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -eo pipefail

start_dotmemory() {
  echo "Starting dotMemory..."

  local args=(--save-to-dir=/nethermind/diag/dotmemory --service-output)

  # Optional periodic snapshots when DIAG_DOTMEMORY_TIMER is set (e.g. "00:00:30" → every 30s).
  # Without this the run only collects the timeline graph, not heap snapshots.
  if [ -n "${DIAG_DOTMEMORY_TIMER:-}" ]; then
    if [[ ! "$DIAG_DOTMEMORY_TIMER" =~ ^[0-9]{2}:[0-9]{2}:[0-9]{2}$ ]]; then
      echo "Error: DIAG_DOTMEMORY_TIMER must be in HH:MM:SS format (got: $DIAG_DOTMEMORY_TIMER)" >&2
      exit 1
    fi
    args+=("--trigger-timer=$DIAG_DOTMEMORY_TIMER")
    if [ -n "${DIAG_DOTMEMORY_MAX_SNAPSHOTS:-}" ]; then
      if [[ ! "$DIAG_DOTMEMORY_MAX_SNAPSHOTS" =~ ^[1-9][0-9]*$ ]]; then
        echo "Error: DIAG_DOTMEMORY_MAX_SNAPSHOTS must be a positive integer (got: $DIAG_DOTMEMORY_MAX_SNAPSHOTS)" >&2
        exit 1
      fi
      args+=("--trigger-max-snapshots=$DIAG_DOTMEMORY_MAX_SNAPSHOTS")
    fi
  elif [ -n "${DIAG_DOTMEMORY_MAX_SNAPSHOTS:-}" ]; then
    echo "Warning: DIAG_DOTMEMORY_MAX_SNAPSHOTS is set but DIAG_DOTMEMORY_TIMER is not; snapshot cap will be ignored." >&2
  fi

  exec dotmemory start "${args[@]}" ./nethermind -- "$@"
}

start_dotnet_trace() {
  echo "Starting dotnet-trace..."

  exec dotnet-trace collect \
    -o /nethermind/diag/dotnet.nettrace \
    --show-child-io \
    -- ./nethermind "$@"
}

start_dottrace() {
  echo "Starting dotTrace..."

  exec dottrace start \
    --framework=netcore \
    --profiling-type=timeline \
    --propagate-exit-code \
    --save-to=/nethermind/diag/dottrace \
    --service-output=on \
    -- ./nethermind "$@"
}

case "$DIAG_WITH" in
  "")
    exec ./nethermind "$@"
    ;;
  dotmemory)
    start_dotmemory "$@"
    ;;
  dotnet-trace)
    start_dotnet_trace "$@"
    ;;
  dottrace)
    start_dottrace "$@"
    ;;
  *)
    printf '\e[31mUnknown DIAG_WITH value: %q\e[0m\n' "$DIAG_WITH" >&2
    exit 2
    ;;
esac
