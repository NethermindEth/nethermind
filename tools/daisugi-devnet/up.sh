#!/usr/bin/env bash
# Starts the devnet on a gen-genesis.sh output. Usage: up.sh <genesis-dir> <nethermind-image> <lighthouse-image>
set -euo pipefail
cd "$(dirname "$0")"
export GENESIS_DIR=$(realpath "${1:?genesis dir}") NETHERMIND_IMAGE=${2:?nethermind image} LIGHTHOUSE_IMAGE=${3:?lighthouse image}
export DEVNET_USER="$(id -u):$(id -g)"
mkdir -p "$GENESIS_DIR"/{nethermind,beacon}
docker compose up -d
