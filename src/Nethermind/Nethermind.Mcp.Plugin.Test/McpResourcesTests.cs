// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class McpResourcesTests
{
    private static readonly string[] ResourceUris = [McpResources.ChainUri, McpResources.ContractsUri, McpResources.GuideUri];

    [Test]
    public async Task Resources_are_listed_and_readable_over_mcp()
    {
        await using McpTestNode node = await McpTestNode.Create();
        await using McpClient client = await node.CreateClient();

        IList<McpClientResource> resources = await client.ListResourcesAsync();
        Assert.That(resources.Select(static r => r.Uri), Is.EquivalentTo(ResourceUris));

        foreach (McpClientResource resource in resources)
        {
            ReadResourceResult read = await client.ReadResourceAsync(resource.Uri);
            TextResourceContents text = (TextResourceContents)read.Contents.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(text.Uri, Is.EqualTo(resource.Uri));
                Assert.That(text.MimeType, Is.EqualTo(resource.MimeType));
                Assert.That(text.Text, Is.Not.Empty);
            }

            if (text.MimeType == "application/json") Assert.That(() => JsonDocument.Parse(text.Text).Dispose(), Throws.Nothing, resource.Uri);
        }
    }

    [Test]
    public async Task Chain_resource_describes_the_running_chain()
    {
        await using McpTestNode node = await McpTestNode.Create(start: false);
        McpResources resources = node.Chain.Container.Resolve<McpResources>();

        using JsonDocument document = JsonDocument.Parse(resources.GetChainJson());
        JsonElement chain = document.RootElement;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.GetProperty("chainId").GetUInt64(), Is.EqualTo(node.Chain.SpecProvider.ChainId));
            Assert.That(chain.GetProperty("nativeCurrency").GetProperty("symbol").GetString(), Is.EqualTo("ETH"));
            Assert.That(chain.GetProperty("nativeCurrency").GetProperty("decimals").GetInt32(), Is.EqualTo(18));
            Assert.That(chain.GetProperty("genesisHash").GetString(), Is.EqualTo(node.Chain.BlockTree.Genesis!.Hash!.ToString()));
            Assert.That(chain.GetProperty("headBlockNumber").GetInt64(), Is.EqualTo(node.Chain.BlockTree.Head!.Number));
            Assert.That(chain.GetProperty("currentFork").GetString(), Is.EqualTo("Berlin"), "the test chain runs Berlin");
            Assert.That(chain.GetProperty("wellKnownContracts").ValueKind, Is.EqualTo(JsonValueKind.Array));
        }
    }

    [Test]
    public void Gnosis_chain_resource_uses_xdai_and_gno()
    {
        McpResources resources = CreateResources(GnosisSpecProvider.Instance, "GnosisChain");

        using JsonDocument document = JsonDocument.Parse(resources.GetChainJson());
        JsonElement chain = document.RootElement;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.GetProperty("chainId").GetUInt64(), Is.EqualTo(BlockchainIds.Gnosis));
            Assert.That(chain.GetProperty("isGnosisFamily").GetBoolean(), Is.True);
            Assert.That(chain.GetProperty("isTestnet").GetBoolean(), Is.False);
            Assert.That(chain.GetProperty("nativeCurrency").GetProperty("symbol").GetString(), Is.EqualTo("xDAI"));
            Assert.That(chain.GetProperty("nativeCurrency").GetProperty("name").GetString(), Is.EqualTo("xDAI"));
            Assert.That(chain.GetProperty("stakingToken").GetProperty("symbol").GetString(), Is.EqualTo("GNO"));
            Assert.That(chain.GetProperty("currentFork").ValueKind, Is.EqualTo(JsonValueKind.Null), "no head yet");
            Assert.That(chain.GetProperty("headBlockNumber").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
    }

    [Test]
    public void Contracts_resource_lists_system_contracts_and_tokens_with_checksummed_addresses()
    {
        McpResources resources = CreateResources(MainnetSpecProvider.Instance, "Ethereum");

        using JsonDocument document = JsonDocument.Parse(resources.GetContractsJson());
        JsonElement[] contracts = [.. document.RootElement.GetProperty("contracts").EnumerateArray()];
        JsonElement usdc = contracts.Single(static c => c.GetProperty("name").GetString() == "USD Coin");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(contracts.Select(static c => c.GetProperty("kind").GetString()), Does.Contain("system").And.Contain("token").And.Contain("ens"));
            Assert.That(usdc.GetProperty("address").GetString(), Is.EqualTo("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48"));
            Assert.That(usdc.GetProperty("symbol").GetString(), Is.EqualTo("USDC"));
            Assert.That(usdc.GetProperty("decimals").GetInt32(), Is.EqualTo(6));
            Assert.That(contracts.Where(static c => c.GetProperty("kind").GetString() == "system").All(static c => !c.TryGetProperty("decimals", out _)), Is.True);
        }
    }

    [Test]
    public void Guide_covers_tools_errors_and_limits()
    {
        McpResources resources = CreateResources(MainnetSpecProvider.Instance, "Ethereum", new McpConfig { MaxLogBlockRange = 1234 });

        string guide = resources.GetGuide();
        using (Assert.EnterMultipleScope())
        {
            foreach (string tool in new[] { "node_status", "explain_transaction", "trace_transaction", "token_balances", "fee_estimate", "get_logs" })
                Assert.That(guide, Does.Contain($"`{tool}`"));
            foreach (string code in new[] { "invalid_input", "not_found", "execution_reverted", "unavailable", "resource_exhausted", "timeout", "internal_error" })
                Assert.That(guide, Does.Contain($"`{code}`"));
            Assert.That(guide, Does.Contain("1234 blocks"), "limits come from the node config");
            Assert.That(guide, Does.Not.Contain("Gnosis Chain notes"));
        }
    }

    [Test]
    public void Guide_has_gnosis_notes_on_gnosis()
    {
        string guide = CreateResources(GnosisSpecProvider.Instance, "GnosisChain").GetGuide();
        Assert.That(guide, Does.Contain("Gnosis Chain notes").And.Contain("xDAI").And.Contain("GNO"));
    }

    [TestCase(BlockchainIds.Mainnet, "ETH", "Ether", "ETH", false, 8)]
    [TestCase(BlockchainIds.Gnosis, "xDAI", "xDAI", "GNO", false, 5)]
    [TestCase(BlockchainIds.Chiado, "xDAI", "xDAI", "GNO", true, 0)]
    [TestCase(BlockchainIds.Sepolia, "ETH", "Ether", "ETH", true, 0)]
    [TestCase(BlockchainIds.Hoodi, "ETH", "Ether", "ETH", true, 0)]
    public void Chain_profile_per_network(ulong chainId, string symbol, string name, string staking, bool testnet, int tokens)
    {
        McpChainProfile profile = new(new ChainSpec(), new TestSpecProvider(Specs.Forks.Prague.Instance) { ChainId = chainId });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile.NativeCurrencySymbol, Is.EqualTo(symbol));
            Assert.That(profile.NativeCurrencyName, Is.EqualTo(name));
            Assert.That(profile.StakingTokenSymbol, Is.EqualTo(staking));
            Assert.That(profile.IsTestnet, Is.EqualTo(testnet));
            Assert.That(profile.Tokens, Has.Count.EqualTo(tokens));
            Assert.That(profile.Tokens.All(static t => t is { Kind: "token", Symbol: not null, Decimals: > 0 }), Is.True);
            Assert.That(profile.WellKnownContracts.Where(static c => c.Kind == "token"), Is.EquivalentTo(profile.Tokens));
        }
    }

    [TestCase("gnosis", "0x0B98057eA310F4d31F2a452B414647007d1645d9", "GnosisChain", "xDAI")]
    [TestCase("chiado", "0xb97036A26259B7147018913bD58a774cf91acf25", null, "xDAI")]
    [TestCase("foundation", "0x00000000219ab540356cBB839Cbe05303d7705Fa", null, "ETH")]
    public void Deposit_contract_and_currency_come_from_the_shipped_chain_spec(string chain, string depositContract, string? name, string symbol)
    {
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"../../../../Chains/{chain}.json"));
        McpChainProfile profile = new(chainSpec, new ChainSpecBasedSpecProvider(chainSpec));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile.DepositContractAddress, Is.EqualTo(new Address(depositContract)));
            Assert.That(profile.WellKnownContracts.Single(static c => c.Name.StartsWith("Beacon deposit contract", StringComparison.Ordinal)).Address,
                Is.EqualTo(profile.DepositContractAddress));
            Assert.That(profile.NativeCurrencySymbol, Is.EqualTo(symbol));
            if (name is not null) Assert.That(profile.NetworkName, Is.EqualTo(name));
        }
    }

    [Test]
    public void Guide_states_the_effective_call_gas_limit()
    {
        McpResources resources = CreateResources(MainnetSpecProvider.Instance, "Ethereum",
            new McpConfig { MaxCallGas = 80_000_000 }, new JsonRpcConfig { GasCap = 30_000_000 });

        Assert.That(resources.GetGuide(), Does.Contain("at most 30000000 gas"));
    }

    [Test]
    public void Token_addresses_are_unique_per_chain([Values(BlockchainIds.Mainnet, BlockchainIds.Gnosis)] ulong chainId)
    {
        McpChainProfile profile = new(new ChainSpec(), new TestSpecProvider(Specs.Forks.Prague.Instance) { ChainId = chainId });
        Assert.That(profile.Tokens.Select(static t => t.Address), Is.Unique);
    }

    private static McpResources CreateResources(ISpecProvider specProvider, string name, IMcpConfig? config = null, IJsonRpcConfig? rpcConfig = null)
    {
        ChainSpec chainSpec = new() { Name = name, ChainId = specProvider.ChainId };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);
        return new McpResources(new McpChainProfile(chainSpec, specProvider), blockTree, specProvider, chainSpec, config ?? new McpConfig(), rpcConfig ?? new JsonRpcConfig());
    }
}
