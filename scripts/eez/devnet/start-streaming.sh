#!/usr/bin/env bash
set -euo pipefail
umask 077
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
profile="${EEZ_CI_PROFILE:-rollup0}"
prover_args=()
if [[ "$#" -lt 2 || "$1" != --listen-addr=0.0.0.0:50061 || "$2" != --chain-config=/out/l2-genesis.json ]]; then
    echo "upstream prover arguments changed" >&2
    exit 1
fi
if [[ "$profile" == rollup0 ]]; then
    if [[ "$#" != 2 ]]; then echo "upstream prover arguments changed" >&2; exit 1; fi
elif [[ "$profile" == rollupx ]]; then
    if [[ "$#" -lt 3 || "$#" -gt 4 || "$3" != --l1-chain-config=/genesis/genesis.json ]]; then
        echo "upstream prover arguments changed" >&2
        exit 1
    fi
    if [[ "$#" == 4 ]]; then
        if [[ ! "$4" =~ ^--max-request-blocks=[1-9][0-9]{0,8}$ ]]; then
            echo "upstream prover arguments changed" >&2
            exit 1
        fi
        prover_args+=("$4")
    fi
else
    echo "unexpected live profile" >&2
    exit 1
fi
stream="${EEZ_KEYSTORE_DIRECTORY:?}/streaming"
mkdir -p "$stream"
openssl rand -base64 32 > "$stream/password"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=eez-ci-server \
    -addext subjectAltName=DNS:eez-ci-server -addext extendedKeyUsage=serverAuth \
    -keyout "$stream/server.key" -out "$stream/server.crt" 2>/dev/null
openssl pkcs12 -export -inkey "$stream/server.key" -in "$stream/server.crt" \
    -out "$stream/server.pfx" -passout "file:$stream/password"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=eez-ci-composer \
    -addext extendedKeyUsage=clientAuth -keyout "$stream/client.key" -out "$stream/client.crt" 2>/dev/null
openssl x509 -in "$stream/client.crt" -outform DER -out "$stream/client.cer"
cat "$stream/client.crt" "$stream/client.key" > "$stream/client.pem"
if [[ "$profile" == rollupx ]]; then
    l1_chain_id="$(jq -er '.config.chainId | select(type == "number" and . > 0 and . == floor)' /genesis/genesis.json)"
    jq -n --argjson l1ChainId "$l1_chain_id" --arg registry "${EEZ_REGISTRY_ADDRESS:?}" \
        --arg manager "${EEZ_ROLLUP_MANAGER_ADDRESS:?}" --arg genesis "${EEZ_INITIAL_BLOCK_HASH:?}" \
        '{profile: "rollupx-native", listenAddress: "127.0.0.1:50062",
        l1ChainId: $l1ChainId, registry: $registry, manager: $manager, l2GenesisHash: $genesis,
        l1ChainConfigPath: "/genesis/genesis.json",
        serverCertificate: "server.pfx", serverPasswordFile: "password", composerCertificates: ["client.cer"],
        maxSessionsPerOwner: 4, maxReservedBytes: 2147483648}' > "$stream/config.json"
else
    jq -n '{profile: "rollup0-conditional", listenAddress: "127.0.0.1:50062",
        serverCertificate: "server.pfx", serverPasswordFile: "password", composerCertificates: ["client.cer"],
        maxSessionsPerOwner: 4, maxReservedBytes: 2147483648}' > "$stream/config.json"
fi
cat > "$stream/haproxy.cfg" <<EOF
global
    maxconn 8
    log stdout format raw local0
defaults
    mode http
    log global
    option httplog
    timeout connect 5s
    timeout client 120s
    timeout server 120s
frontend composer
    bind :50061 proto h2
    http-request deny unless { path /prove.v2.Prover/ProveStream /prove.v2.Prover/Capabilities }
    default_backend prover
backend prover
    http-request set-uri https://eez-ci-server%[pathq]
    server candidate 127.0.0.1:50062 ssl alpn h2 verify required ca-file $stream/server.crt verifyhost eez-ci-server sni str(eez-ci-server) crt $stream/client.pem
EOF
haproxy -c -f "$stream/haproxy.cfg"
prover_pid=
proxy_pid=
cleanup() {
    trap - EXIT INT TERM
    if [[ -n "$proxy_pid" ]]; then kill "$proxy_pid" 2>/dev/null || true; fi
    if [[ -n "$prover_pid" ]]; then kill "$prover_pid" 2>/dev/null || true; fi
    for ((attempt=0; attempt<50; attempt++)); do
        if [[ -z "$(jobs -pr)" ]]; then break; fi
        sleep 0.1
    done
    for child in $(jobs -pr); do kill -KILL "$child" 2>/dev/null || true; done
    wait || true
}
trap cleanup EXIT
trap 'exit 143' TERM
trap 'exit 130' INT
"$HERE/nethermind-eez-attester" --listen-addr=127.0.0.1:50063 \
    --chain-config=/out/l2-genesis.json --streaming-config="$stream/config.json" "${prover_args[@]}" &
prover_pid=$!
haproxy -db -f "$stream/haproxy.cfg" > "$stream/haproxy.log" 2>&1 &
proxy_pid=$!
set +e
wait -n "$prover_pid" "$proxy_pid"
status=$?
set -e
if [[ "$status" == 0 ]]; then status=1; fi
exit "$status"
