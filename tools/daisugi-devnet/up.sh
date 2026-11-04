#!/usr/bin/env bash
# Starts the devnet on a gen-genesis.sh output. Usage: up.sh <genesis-dir> <nethermind-image> <lighthouse-image>
set -euo pipefail
cd "$(dirname "$0")"
export GENESIS_DIR=$(realpath "${1:?genesis dir}") NETHERMIND_IMAGE=${2:?nethermind image} LIGHTHOUSE_IMAGE=${3:?lighthouse image}
export DEVNET_USER="$(id -u):$(id -g)"
P="$GENESIS_DIR/params.json"
fail() { echo "$*" >&2; exit 1; }
PUBLIC_FEE_RECIPIENTS="0x8943545177806ed17b9f23f0a21ee5948ecaa776 0xf39fd6e51aad88f6f4ce6ab8827279cfffb92266 0x0000000000000000000000000000000000000000"

if [[ $(jq -r 'if has("local") then .local else true end' "$P") == true ]]; then
  OPERATOR=${OPERATOR:-$(jq -r '.operators[0].name // "default"' "$P")}
  FEE_RECIPIENT=${FEE_RECIPIENT:-0x8943545177806ED17B9F23F0a21ee5948eCaa776}
else
  [[ -n "${OPERATOR:-}" ]] || fail "OPERATOR is required on a shared network: one of $(jq -r '[.operators[].name] | join(", ")' "$P")"
  [[ "${FEE_RECIPIENT:-}" =~ ^0x[0-9a-fA-F]{40}$ ]] || fail "FEE_RECIPIENT (0x address you control) is required on a shared network"
  [[ " $PUBLIC_FEE_RECIPIENTS " != *" ${FEE_RECIPIENT,,} "* ]] || fail "FEE_RECIPIENT $FEE_RECIPIENT has a public private key"
fi
DIR=$(jq -r --arg o "$OPERATOR" '(.operators // [{name: $o, dir: "data/validators"}])[] | select(.name == $o) | .dir' "$P")
[[ -n "$DIR" ]] || fail "operator $OPERATOR is not in $P"
export VALIDATOR_DIR="$GENESIS_DIR/$DIR" FEE_RECIPIENT
[[ -d "$VALIDATOR_DIR/keys" && -d "$VALIDATOR_DIR/secrets" ]] || fail "no keystores for $OPERATOR in $VALIDATOR_DIR"
mkdir -p "$GENESIS_DIR"/{nethermind,beacon}
if [[ ! -s "$GENESIS_DIR/data/jwt/jwtsecret" ]]; then
  JWT=$(openssl rand -hex 32)
  mkdir -p "$GENESIS_DIR/data/jwt" && printf "0x%s" "$JWT" > "$GENESIS_DIR/data/jwt/jwtsecret"
fi
docker compose up -d
