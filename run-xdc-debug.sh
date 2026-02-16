#!/bin/bash
# Launch Nethermind with XDC mainnet + debug logging

cd "$(dirname "$0")"

# XDC mainnet bootnodes (from chainspec)
BOOTNODES="enode://91e59fa1b034ae35e9f4e8a99cc6621f09d74e76a6220abb6c93b29ed41a9e1fc4e5b70e2c5fc43f883cffbdcd6f4f6cbc1d23af077f28c2aecc22403355d4b1@81.0.220.137:30304,enode://91e59fa1b034ae35e9f4e8a99cc6621f09d74e76a6220abb6c93b29ed41a9e1fc4e5b70e2c5fc43f883cffbdcd6f4f6cbc1d23af077f28c2aecc22403355d4b1@5.189.144.192:30304,enode://91e59fa1b034ae35e9f4e8a99cc6621f09d74e76a6220abb6c93b29ed41a9e1fc4e5b70e2c5fc43f883cffbdcd6f4f6cbc1d23af077f28c2aecc22403355d4b1@154.53.42.5:30304"

dotnet run --project src/Nethermind/Nethermind.Runner -- \
  --config xdc \
  --log DEBUG \
  --Init.LogLevel DEBUG \
  --Network.Bootnodes "$BOOTNODES" \
  --JsonRpc.Enabled true \
  --JsonRpc.Host 0.0.0.0 \
  --JsonRpc.Port 8545 \
  "$@"
