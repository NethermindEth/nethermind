#!/usr/bin/env bash

# Maps an L2 network onto the L1 network it derives from: op-sepolia and world-sepolia both
# run against sepolia, op-mainnet and world-mainnet against mainnet. Networks that are not
# L2s are returned unchanged.
resolve_l1_network() {
  local network="${1#op-}"
  printf '%s' "${network#world-}"
}

# World Chain runs on the OP stack and needs an explicit chain selection on top of it.
is_worldchain() {
  [[ "$1" == world-* ]]
}
