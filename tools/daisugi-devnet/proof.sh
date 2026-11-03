#!/usr/bin/env bash
# EIP-8288 proof run on a devnet started by up.sh.
#   proof.sh send <genesis-dir>         (EL_RPC selects the receiving EL, default the original one)
#       Submits a frame transaction with leanSig and leanSTARK dependencies through eth_sendProofWrapper, after checking
#       that corrupted wrappers are rejected. Writes <genesis-dir>/proof/send.json.
#   proof.sh replay <genesis-dir> <nethermind-image> <proof-slot>
#       Starts an empty Nethermind with no peers and imports every canonical payload from the beacon node, sending
#       tampered copies of <proof-slot> first. Writes <genesis-dir>/proof/replay.json.
# Needs the native test build for the leanSig signer and fixtures:
#   dotnet test --project src/Nethermind/Nethermind.Crypto.LeanFfi.Test -c release -p:BuildLeanFfi=true
set -euo pipefail
cd "$(dirname "$0")"
G=$(realpath "${2:?genesis dir}")
TOOL=(dotnet run -c release --file proof-tool.cs --)
VECTORS=$(realpath ../../src/Nethermind/artifacts/bin/Nethermind.Crypto.LeanFfi.Test/release/lean-vectors)
mkdir -p "$G/proof"

case "$1" in
  send)
    GENERATOR=$(grep -oP '^GENERATOR=\K.*' gen-genesis.sh)
    MNEMONIC=$(cat "$G/mnemonic")
    # First account premined from the devnet mnemonic.
    DEVNET_SENDER_KEY=$(docker run --rm --network none --entrypoint geth-hdwallet "$GENERATOR" \
      -mnemonic "$MNEMONIC" -path "m/44'/60'/0'/0/0" | awk '/private key/{print $3}') \
      "${TOOL[@]}" send "${EL_RPC:-http://127.0.0.1:28545}" "$VECTORS" "$G/proof" ;;
  replay)
    mkdir -p "$G/nethermind-replay"
    GENESIS_DIR=$G NETHERMIND_IMAGE=${3:?nethermind image} LIGHTHOUSE_IMAGE=unused DEVNET_USER="$(id -u):$(id -g)" \
      docker compose --profile replay up -d nethermind-replay
    until curl -sf -o /dev/null -X POST -H 'content-type: application/json' \
      --data '{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}' http://127.0.0.1:28546; do sleep 2; done
    "${TOOL[@]}" replay http://127.0.0.1:25052 http://127.0.0.1:28551 "$G/data/jwt/jwtsecret" "$G/proof" "${4:?proof slot}" ;;
  *) echo "usage: proof.sh send|replay ..." >&2; exit 1 ;;
esac
