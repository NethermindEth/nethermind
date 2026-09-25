// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Chain facts tools use to phrase results correctly: network name, native currency and well-known contracts.</summary>
/// <remarks>Values come from the running chain spec where possible, so they follow the node's actual network.</remarks>
public sealed class McpChainProfile(ChainSpec chainSpec, ISpecProvider specProvider)
{
    private static readonly Address EnsRegistry = new("0x00000000000C2E074eC69A0dFb2997BA6C7d2e1e");

    /// <summary>Gets the chain ID.</summary>
    public ulong ChainId => specProvider.ChainId;

    /// <summary>Gets the network name from the chain spec, such as <c>Foundation</c> for mainnet.</summary>
    public string NetworkName => chainSpec.Name ?? $"chain {specProvider.ChainId}";

    /// <summary>Gets whether this is Gnosis Chain or its Chiado testnet, whose native currency is xDAI and whose staking uses GNO.</summary>
    public bool IsGnosisFamily => specProvider.ChainId is BlockchainIds.Gnosis or BlockchainIds.Chiado;

    /// <summary>Gets the native currency symbol: <c>xDAI</c> on Gnosis chains, otherwise <c>ETH</c>.</summary>
    public string NativeCurrencySymbol => IsGnosisFamily ? "xDAI" : "ETH";

    /// <summary>Gets the ENS registry address on networks where the canonical deployment exists, otherwise <see langword="null"/>.</summary>
    public Address? EnsRegistryAddress => specProvider.ChainId is BlockchainIds.Mainnet or BlockchainIds.Sepolia or BlockchainIds.Holesky
        ? EnsRegistry
        : null;

    /// <summary>Gets the beacon-chain deposit contract of the current fork, if any.</summary>
    public Address? DepositContractAddress => specProvider.GetFinalSpec().DepositContractAddress;
}
