#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
case "${EEZ_SUITE:?}" in
    embedded-*) bash "$HERE/embedded.sh" ;;
    contracts)
        cd "${EEZ_UPSTREAM:?}"
        timeout 10m forge test --root contracts --json > "$EEZ_CI_RESULT_DIR/contracts.json"
        ;;
    *)
        if [[ "$EEZ_SUITE" == protocol ]]; then
            EEZ_PROTOCOL_SCENARIOS="$(jq -er --arg suite "$EEZ_SUITE" '.suites[$suite].targets | select(length > 0) | join(" ")' "$EEZ_CI_RESULT_DIR/partitions.json")"
            export EEZ_PROTOCOL_SCENARIOS
        fi
        bash "$HERE/run.sh"
        ;;
esac
python3 "$HERE/partitions.py" report --directory "$EEZ_CI_RESULT_DIR"
