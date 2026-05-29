#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

cp "$SCRIPT_DIR/jwtsecret" /tmp/jwtsecret

# shellcheck source=/dev/null
source "$REPO_ROOT/scripts/common/wait_for_rpc.sh"
# shellcheck source=/dev/null
source "$REPO_ROOT/scripts/common/docker_compose.sh"

rm -f "$SCRIPT_DIR/docker-compose.override.yml"

if [ -f "$SCRIPT_DIR/.env" ]; then
    EXTRA_CLIENT_FLAGS="$(grep -oP '^EXTRA_CLIENT_FLAGS=\K.*' "$SCRIPT_DIR/.env" || true)"
fi

NEED_OVERRIDE=false
OVERRIDE_ENV_LINES=""
OVERRIDE_ENTRYPOINT=""
OVERRIDE_VOLUMES=""

if [ -n "${DIAG_WITH:-}" ]; then
    NEED_OVERRIDE=true
    DIAG_SCRIPT="$SCRIPT_DIR/diag-entrypoint.sh"
    chmod +x "$DIAG_SCRIPT"
    ABS_DIAG_SCRIPT="$(cd "$(dirname "$DIAG_SCRIPT")" && pwd)/$(basename "$DIAG_SCRIPT")"

    OVERRIDE_ENV_LINES="      - DIAG_WITH=${DIAG_WITH}"
    # Forward optional dotMemory snapshot-trigger env vars (consumed by diag-entrypoint.sh)
    if [ -n "${DIAG_DOTMEMORY_TIMER:-}" ]; then
        OVERRIDE_ENV_LINES="${OVERRIDE_ENV_LINES}
      - DIAG_DOTMEMORY_TIMER=${DIAG_DOTMEMORY_TIMER}"
    fi
    if [ -n "${DIAG_DOTMEMORY_MAX_SNAPSHOTS:-}" ]; then
        OVERRIDE_ENV_LINES="${OVERRIDE_ENV_LINES}
      - DIAG_DOTMEMORY_MAX_SNAPSHOTS=${DIAG_DOTMEMORY_MAX_SNAPSHOTS}"
    fi
    OVERRIDE_ENTRYPOINT='    entrypoint: ["./diag-entrypoint.sh"]'
    OVERRIDE_VOLUMES="    volumes:
      - ${ABS_DIAG_SCRIPT}:/nethermind/diag-entrypoint.sh:ro"
    echo "[diag] Override: mount diag-entrypoint.sh + set DIAG_WITH=$DIAG_WITH"
elif [ -n "${EXTRA_CLIENT_FLAGS:-}" ]; then
    NEED_OVERRIDE=true
    OVERRIDE_ENTRYPOINT='    entrypoint: ["/bin/sh", "-c", "exec ./nethermind \"$@\" ${EXTRA_CLIENT_FLAGS}", "--"]'
fi

if [ -n "${EXTRA_CLIENT_FLAGS:-}" ]; then
    NEED_OVERRIDE=true
    if [ -n "$OVERRIDE_ENV_LINES" ]; then
        OVERRIDE_ENV_LINES="${OVERRIDE_ENV_LINES}
      - EXTRA_CLIENT_FLAGS=${EXTRA_CLIENT_FLAGS}"
    else
        OVERRIDE_ENV_LINES="      - EXTRA_CLIENT_FLAGS=${EXTRA_CLIENT_FLAGS}"
    fi
    echo "[extra-flags] Extra Nethermind flags: ${EXTRA_CLIENT_FLAGS}"
fi

if [ "$NEED_OVERRIDE" = true ]; then
    {
        echo "services:"
        echo "  execution:"
        if [ -n "$OVERRIDE_ENV_LINES" ]; then
            echo "    environment:"
            echo "$OVERRIDE_ENV_LINES"
        fi
        [ -n "$OVERRIDE_ENTRYPOINT" ] && echo "$OVERRIDE_ENTRYPOINT"
        [ -n "$OVERRIDE_VOLUMES" ] && echo "$OVERRIDE_VOLUMES"
    } > "$SCRIPT_DIR/docker-compose.override.yml"
    echo "[override] Generated docker-compose.override.yml:"
    cat "$SCRIPT_DIR/docker-compose.override.yml"
fi

pushd "$SCRIPT_DIR" >/dev/null
compose_cmd up --detach
popd >/dev/null

echo "Invoking wait_for_rpc for Nethermind RPC readiness..."
if ! wait_for_rpc "http://0.0.0.0:8545" 300; then
    echo "RPC failed to start. Dumping logs..."
    pushd "$SCRIPT_DIR" >/dev/null
    compose_cmd logs
    popd >/dev/null
    exit 1
fi

pushd "$SCRIPT_DIR" >/dev/null
compose_cmd logs
popd >/dev/null
