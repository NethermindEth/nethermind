#!/usr/bin/env bash
set -euo pipefail
: "${KURTOSIS_ENCLAVE:?}"
: "${KURTOSIS_BUILDER_SERVICE:?}"
wait_for_log() {
    local service="$1" pattern="$2" logs
    local deadline=$((SECONDS + ${EEZ_RELAY_READY_TIMEOUT_SECS:-180}))
    while :; do
        logs="$(timeout 10s kurtosis service logs --all "$KURTOSIS_ENCLAVE" "$service" 2>/dev/null \
            | sed 's/\x1b\[[0-9;]*m//g' || true)"
        if grep -qE "$pattern" <<< "$logs"; then return 0; fi
        (( SECONDS < deadline )) || break
        sleep 5
    done
    echo "relay readiness missing for $service" >&2
    return 1
}
wait_for_log mev-relay-api 'validator registrations call processed.*numRegistrations(New)?=[1-9][0-9]*'
kurtosis service stop "$KURTOSIS_ENCLAVE" mev-relay-housekeeper
kurtosis service start "$KURTOSIS_ENCLAVE" mev-relay-housekeeper
wait_for_log "$KURTOSIS_BUILDER_SERVICE" 'Updated validator registrations.*len=[1-9][0-9]*'
