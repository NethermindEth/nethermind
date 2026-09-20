// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Core;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>Selects the built-in bootnode ENRs for a chain id, keyed the same way as <see cref="BeaconChainSpec.ForChainId"/>.</summary>
public static class BeaconBootnodes
{
    /// <exception cref="UnsupportedBeaconNetworkException"><paramref name="chainId"/> has no built-in bootnode list.</exception>
    public static string[] ForChainId(ulong chainId) => chainId switch
    {
        BlockchainIds.Mainnet => MainnetBootnodes.Enrs,
        BlockchainIds.Hoodi => HoodiBootnodes.Enrs,
        BlockchainIds.Sepolia => SepoliaBootnodes.Enrs,
        _ => throw new UnsupportedBeaconNetworkException(chainId),
    };
}
