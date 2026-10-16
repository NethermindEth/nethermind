#!/usr/bin/env sh
set -eu
umask 077
HERE="$(CDPATH="" cd -- "$(dirname -- "$0")" && pwd)"
: "${EEZ_PROOF_SIGNER_KEY:?}"
: "${EEZ_ATTESTER_ADDRESS:?}"
: "${EEZ_PROOF_SYSTEM:?}"
: "${EEZ_VKEY:?}"
: "${EEZ_ROLLUP_ID:?}"
export EEZ_KEYSTORE_DIRECTORY="${EEZ_KEYSTORE_DIRECTORY:-/data/keystore}"
export EEZ_KEYSTORE_PASSWORD_FILE="${EEZ_KEYSTORE_PASSWORD_FILE:-/data/password}"
export EEZ_SKIP_CONSENSUS_LAYER_CALLS="${EEZ_SKIP_CONSENSUS_LAYER_CALLS:-true}"
export EEZ_L2_BLOCK_TIME_SECS="${EEZ_L2_BLOCK_TIME_SECS:-2}"
mkdir -p "$EEZ_KEYSTORE_DIRECTORY"
if [ ! -f "$EEZ_KEYSTORE_PASSWORD_FILE" ]; then
    printf '%s\n' 'ci-only-public-devnet-key' > "$EEZ_KEYSTORE_PASSWORD_FILE"
fi
imported="$(printf '%s' "$EEZ_PROOF_SIGNER_KEY" | "$HERE/nethermind-eez-attester" keystore-import \
    --keystore-directory "$EEZ_KEYSTORE_DIRECTORY" --password-file "$EEZ_KEYSTORE_PASSWORD_FILE")"
imported="$(printf '%s' "$imported" | tail -n 1 | tr '[:upper:]' '[:lower:]')"
expected="$(printf '%s' "$EEZ_ATTESTER_ADDRESS" | tr '[:upper:]' '[:lower:]')"
if [ "$imported" != "$expected" ]; then
    echo "imported attester does not match the deployed proof system signer" >&2
    exit 1
fi
unset EEZ_PROOF_SIGNER_KEY
if [ "${EEZ_CI_STREAMING:-false}" = true ]; then
    exec /bin/bash "$HERE/start-streaming.sh" "$@"
fi
exec "$HERE/nethermind-eez-attester" "$@"
