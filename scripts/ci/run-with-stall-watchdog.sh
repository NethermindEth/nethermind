#!/usr/bin/env bash
# Run a command and, if it stops producing output, capture diagnostics before killing it.
#
# Usage:
#   run-with-stall-watchdog.sh <command> [args...]
#
# Environment:
#   STALL_SECONDS  Seconds of silence that count as a stall (default 300)
#   DIAG_DIR       Where to write the log and diagnostics (default $RUNNER_TEMP/stall-diagnostics)
#
# Intended for builds that occasionally deadlock: a plain step timeout kills the process tree
# before anything can be inspected, leaving nothing to debug. On a stall this dumps the process
# tree, memory/disk state and managed stacks of every .NET process, so the artifact is enough to
# tell what the build was waiting on.

set -uo pipefail

STALL_SECONDS="${STALL_SECONDS:-300}"
DIAG_DIR="${DIAG_DIR:-${RUNNER_TEMP:-/tmp}/stall-diagnostics}"
POLL_SECONDS="${POLL_SECONDS:-15}"
DIAGNOSTIC_SECONDS="${DIAGNOSTIC_SECONDS:-120}"
INSTALL_TIMEOUT_SECONDS="${INSTALL_TIMEOUT_SECONDS:-30}"
STACK_TIMEOUT_SECONDS="${STACK_TIMEOUT_SECONDS:-10}"
TERMINATION_SECONDS="${TERMINATION_SECONDS:-30}"

mkdir -p "$DIAG_DIR"
log="$DIAG_DIR/command.log"
: > "$log"

# setsid puts the command in its own process group so a stall can take the whole tree down.
setsid --wait bash -c 'printf "%s\n" "$$" > "$1"; shift; exec "$@"' bash "$DIAG_DIR/command.pid" "$@" > "$log" 2>&1 &
command_pid=$!

# --pid makes tail drain the log and exit once the command is gone.
tail -n +1 --pid="$command_pid" -F "$log" 2>/dev/null &
tail_pid=$!

collect_diagnostics() {
    echo "::error::No output for ${STALL_SECONDS}s — collecting diagnostics into $DIAG_DIR"

    {
        date -u +"stalled at %Y-%m-%dT%H:%M:%SZ"
        echo "=== uptime / load ==="; uptime
        echo "=== memory ==="; free -m
        echo "=== disk ==="; df -h
        echo "=== process tree ==="; ps -ef --forest
        echo "=== threads ==="; ps -eLf
    } > "$DIAG_DIR/system.txt" 2>&1

    # Managed stacks show which target or task each MSBuild node and the compiler server sit in.
    local deadline=$((SECONDS + DIAGNOSTIC_SECONDS))
    local install_seconds=$INSTALL_TIMEOUT_SECONDS
    ((install_seconds > DIAGNOSTIC_SECONDS)) && install_seconds=$DIAGNOSTIC_SECONDS
    timeout --kill-after=1s "${install_seconds}s" dotnet tool install --global dotnet-stack > "$DIAG_DIR/dotnet-stack-install.log" 2>&1 || true
    export PATH="$PATH:$HOME/.dotnet/tools"
    if command -v dotnet-stack > /dev/null; then
        for pid in $(pgrep -x 'dotnet|VBCSCompiler'); do
            local remaining=$((deadline - SECONDS))
            ((remaining > 0)) || break
            ((remaining > STACK_TIMEOUT_SECONDS)) && remaining=$STACK_TIMEOUT_SECONDS
            timeout --kill-after=1s "${remaining}s" dotnet-stack report --process-id "$pid" > "$DIAG_DIR/stack-$pid.txt" 2>&1 || true
        done
    fi

    if [[ -n "${MSBUILDDEBUGPATH:-}" && -d "$MSBUILDDEBUGPATH" ]]; then
        mkdir -p "$DIAG_DIR/msbuild-debug"
        # SchedulerState grows to hundreds of MB; only its tail describes the wedged build.
        for file in "$MSBUILDDEBUGPATH"/*; do
            [[ -f "$file" ]] || continue
            tail -c 5000000 "$file" > "$DIAG_DIR/msbuild-debug/$(basename "$file")"
        done
    fi

    # Forced termination leaves the binlog's gzip stream unfinished; use stacks and scheduler logs.
    local command_group
    command_group=$(cat "$DIAG_DIR/command.pid" 2>/dev/null) || command_group=$command_pid
    [[ "$command_group" =~ ^[1-9][0-9]*$ ]] || command_group=$command_pid
    kill -- "-$command_group" 2>/dev/null || kill "$command_pid" 2>/dev/null
    for ((second=0; second<TERMINATION_SECONDS; second++)); do
        kill -0 -- "-$command_group" 2>/dev/null || return
        sleep 1
    done
    kill -9 -- "-$command_group" 2>/dev/null || kill -9 "$command_pid" 2>/dev/null
}

last_size=-1
silent_for=0

while kill -0 "$command_pid" 2>/dev/null; do
    sleep "$POLL_SECONDS"

    size=$(stat -c %s "$log" 2>/dev/null || echo 0)
    if [[ "$size" == "$last_size" ]]; then
        silent_for=$((silent_for + POLL_SECONDS))
    else
        last_size=$size
        silent_for=0
    fi

    if ((silent_for >= STALL_SECONDS)); then
        collect_diagnostics
        wait "$command_pid"
        wait "$tail_pid" 2>/dev/null
        exit 1
    fi
done

wait "$command_pid"
status=$?
wait "$tail_pid" 2>/dev/null
exit $status
