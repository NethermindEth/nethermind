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
    private const string TokenKind = "token";
    private static readonly Address EnsRegistry = new("0x00000000000C2E074eC69A0dFb2997BA6C7d2e1e");

    // Canonical deployments, cross-checked against Etherscan/Gnosisscan token pages and the issuers' own docs.
    // Only addresses with a single, uncontested canonical deployment are listed: a wrong address is worse than a missing one.
    private static readonly McpWellKnownContract[] MainnetTokens =
    [
        Token("Wrapped Ether", "WETH", 18, "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2"), // WETH9
        Token("USD Coin", "USDC", 6, "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48"), // Circle
        Token("Tether USD", "USDT", 6, "0xdAC17F958D2ee523a2206206994597C13D831ec7"), // Tether
        Token("Dai Stablecoin", "DAI", 18, "0x6B175474E89094C44Da98b954EedeAC495271d0F"), // MakerDAO / Sky
        Token("Wrapped BTC", "WBTC", 8, "0x2260FAC5E5542a773Aa44fBCfeDf7C193bc2C599"), // BitGo
        Token("Lido Staked Ether", "stETH", 18, "0xae7ab96520DE3A18E5e111B5EaAb095312D7fE84"), // Lido
        Token("Wrapped liquid staked Ether 2.0", "wstETH", 18, "0x7f39C581F595B53c5cb19bD0b3f8dA6c935E2Ca0"), // Lido
        Token("Gnosis Token", "GNO", 18, "0x6810e776880C02933D47DB1b9fc05908e5386b96"), // Gnosis
    ];

    private static readonly McpWellKnownContract[] GnosisTokens =
    [
        Token("Wrapped xDAI", "WXDAI", 18, "0xe91D153E0b41518A2Ce8Dd3D7944Fa863463a97d"), // Gnosis docs
        Token("Gnosis Token (staking)", "GNO", 18, "0x9C58BAcC331c9aa871AFD802DB6379a98e80CEdb"), // Omnibridge-bridged GNO, Gnosis docs
        Token("USD Coin (bridged)", "USDC", 6, "0xDDAfbb505ad214D7b80b1f830fcCc89B60fb7A83"), // Omnibridge-bridged USDC, Gnosis docs
        Token("Wrapped Ether (bridged)", "WETH", 18, "0x6A023CCd1ff6F2045C3309768eAd9E68F978f6e1"), // Omnibridge-bridged WETH, Gnosis docs
        Token("Savings xDAI", "sDAI", 18, "0xaf204776c7245bF4147c2612BF6e5972Ee483701"), // Spark / Gnosis docs
    ];

    /// <summary>Gets the chain ID.</summary>
    public ulong ChainId => specProvider.ChainId;

    /// <summary>Gets the network name from the chain spec, such as <c>Foundation</c> for mainnet.</summary>
    public string NetworkName => chainSpec.Name ?? $"chain {specProvider.ChainId}";

    /// <summary>Gets whether this is Gnosis Chain or its Chiado testnet, whose native currency is xDAI and whose staking uses GNO.</summary>
    public bool IsGnosisFamily => specProvider.ChainId is BlockchainIds.Gnosis or BlockchainIds.Chiado;

    /// <summary>Gets whether this is a well-known public testnet (Sepolia, Holesky, Hoodi or Chiado), whose coins have no value.</summary>
    public bool IsTestnet => specProvider.ChainId is BlockchainIds.Sepolia or BlockchainIds.Holesky or BlockchainIds.Hoodi or BlockchainIds.Chiado;

    /// <summary>Gets the native currency symbol: <c>xDAI</c> on Gnosis chains, otherwise <c>ETH</c>.</summary>
    public string NativeCurrencySymbol => IsGnosisFamily ? "xDAI" : "ETH";

    /// <summary>Gets the native currency name: <c>xDAI</c> on Gnosis chains, otherwise <c>Ether</c>.</summary>
    public string NativeCurrencyName => IsGnosisFamily ? "xDAI" : "Ether";

    /// <summary>Gets the number of decimals of the native currency (18 on every supported chain).</summary>
    public int NativeCurrencyDecimals => 18;

    /// <summary>Gets the symbol of the token validators stake: <c>GNO</c> on Gnosis chains, otherwise <c>ETH</c>.</summary>
    public string StakingTokenSymbol => IsGnosisFamily ? "GNO" : "ETH";

    /// <summary>Gets the ENS registry address on networks where the canonical deployment exists, otherwise <see langword="null"/>.</summary>
    public Address? EnsRegistryAddress => specProvider.ChainId is BlockchainIds.Mainnet or BlockchainIds.Sepolia or BlockchainIds.Holesky
        ? EnsRegistry
        : null;

    /// <summary>Gets the beacon-chain deposit contract of the current fork, if any.</summary>
    /// <remarks>On Gnosis chains validators deposit GNO, not the native currency, through this contract.</remarks>
    public Address? DepositContractAddress => specProvider.GetFinalSpec().DepositContractAddress;

    /// <summary>Gets well-known contracts of this chain: system contracts from the spec plus notable tokens.</summary>
    /// <remarks>System contracts come from the latest fork spec; tokens (kind <c>token</c>, with symbol and decimals) are listed for
    /// Ethereum mainnet and Gnosis Chain only.</remarks>
    public IReadOnlyList<McpWellKnownContract> WellKnownContracts
    {
        get
        {
            IReleaseSpec spec = specProvider.GetFinalSpec();
            List<McpWellKnownContract> contracts = [];
            Add(contracts, IsGnosisFamily ? "Beacon deposit contract (GNO staking)" : "Beacon deposit contract", spec.DepositContractAddress, "system");
            Add(contracts, "EIP-4788 beacon roots", spec.Eip4788ContractAddress, "system");
            Add(contracts, "EIP-2935 block hash history", spec.Eip2935ContractAddress, "system");
            Add(contracts, "EIP-7002 withdrawal requests", spec.Eip7002ContractAddress, "system");
            Add(contracts, "EIP-7251 consolidation requests", spec.Eip7251ContractAddress, "system");
            Add(contracts, "ENS registry", EnsRegistryAddress, "ens");
            contracts.AddRange(Tokens);
            return contracts;

            static void Add(List<McpWellKnownContract> list, string name, Address? address, string kind)
            {
                if (address is not null) list.Add(new McpWellKnownContract(name, address, kind));
            }
        }
    }

    /// <summary>Gets the well-known tokens of this chain (kind <c>token</c>), empty when none are listed.</summary>
    public IReadOnlyList<McpWellKnownContract> Tokens => specProvider.ChainId switch
    {
        BlockchainIds.Mainnet => MainnetTokens,
        BlockchainIds.Gnosis => GnosisTokens,
        _ => [],
    };

    private static McpWellKnownContract Token(string name, string symbol, int decimals, string address) =>
        new(name, new Address(address), TokenKind) { Symbol = symbol, Decimals = decimals };
}

/// <summary>A well-known contract on the running chain.</summary>
/// <param name="Name">A human-readable name.</param>
/// <param name="Address">The contract address.</param>
/// <param name="Kind"><c>system</c>, <c>token</c>, <c>ens</c> or another short category.</param>
public sealed record McpWellKnownContract(string Name, Address Address, string Kind)
{
    /// <summary>Gets the token symbol, for <c>token</c> entries.</summary>
    public string? Symbol { get; init; }

    /// <summary>Gets the token decimals, for <c>token</c> entries.</summary>
    public int? Decimals { get; init; }
}
