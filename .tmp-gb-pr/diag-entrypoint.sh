#!/bin/bash
set -eo pipefail

if [ -n "${EXTRA_CLIENT_FLAGS:-}" ]; then
    # shellcheck disable=SC2086
    set -- "$@" $EXTRA_CLIENT_FLAGS
fi

resolve_tool() {
  local name="$1"
  if command -v "$name" >/dev/null 2>&1; then echo "$name"; return; fi
  for dir in /opt/diag-tools /root/.dotnet/tools /usr/local/bin; do
    if [ -x "$dir/$name" ]; then echo "$dir/$name"; return; fi
  done
  echo "[diag] Searching for $name in image:" >&2
  find / -name "$name" -type f 2>/dev/null | head -3 >&2
  echo "$name"
}

start_dottrace() {
  local tool; tool=$(resolve_tool dottrace)
  echo "Starting dotTrace ($tool)..."
  exec "$tool" start \
    --framework=netcore \
    --profiling-type=sampling \
    --propagate-exit-code \
    --save-to=/nethermind/diag/dottrace \
    --service-output=on \
    -- ./nethermind "$@"
}

start_dotmemory() {
  local tool; tool=$(resolve_tool dotmemory)
  echo "Starting dotMemory ($tool)..."

  local args=(--save-to-dir=/nethermind/diag/dotmemory --service-output)

  # Optional periodic snapshots when DIAG_DOTMEMORY_TIMER is set (e.g. "00:00:30" → every 30s).
  # Without this the run only collects the timeline graph, not heap snapshots.
  if [ -n "${DIAG_DOTMEMORY_TIMER:-}" ]; then
    if [[ ! "$DIAG_DOTMEMORY_TIMER" =~ ^[0-9]{2}:[0-9]{2}:[0-9]{2}$ ]]; then
      echo "Error: DIAG_DOTMEMORY_TIMER must be in HH:MM:SS format (got: $DIAG_DOTMEMORY_TIMER)" >&2
      exit 1
    fi
    args+=("--trigger-on-timer=$DIAG_DOTMEMORY_TIMER")
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

  exec "$tool" start "${args[@]}" ./nethermind -- "$@"
}

start_dotnet_trace() {
  local tool; tool=$(resolve_tool dotnet-trace)
  echo "Starting dotnet-trace ($tool)..."
  exec "$tool" collect \
    -o /nethermind/diag/dotnet.nettrace \
    --show-child-io \
    -- ./nethermind "$@"
}

case "${DIAG_WITH:-}" in
  "")
    exec ./nethermind "$@"
    ;;
  dottrace)
    start_dottrace "$@"
    ;;
  dotmemory)
    start_dotmemory "$@"
    ;;
  dotnet-trace)
    start_dotnet_trace "$@"
    ;;
  *)
    printf '\e[31mUnknown DIAG_WITH value: %q\e[0m\n' "$DIAG_WITH" >&2
    exit 2
    ;;
esac
