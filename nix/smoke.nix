# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

{ nodeCheck }:
nodeCheck {
  name = "nethermind-smoke-check";
  nodeArgs = "-c mainnet --Init.DiscoveryEnabled false --Init.PeerManagerEnabled false";
  chainId = "0x1";
}
