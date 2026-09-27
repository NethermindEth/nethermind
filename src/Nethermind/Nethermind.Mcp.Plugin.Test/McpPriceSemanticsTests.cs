// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.Self)]
public class McpPriceSemanticsTests
{
    private static readonly Address NativeFeed = new("0x5f4eC3Df9cbd43714FE2740f5E3616155c5b8419");
    private static readonly Address Usdc = new("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48");

    [TestCase(true, 10, 3600, true, false)]
    [TestCase(true, -1, 3600, false, false)]
    [TestCase(false, -30, 3600, true, false)]
    [TestCase(false, -61, 3600, false, false)]
    [TestCase(false, 3780, 3600, true, false)]
    [TestCase(false, 3781, 3600, true, true)]
    [TestCase(false, 160, 100, true, false)]
    [TestCase(false, 161, 100, true, true)]
    public void Price_freshness_uses_block_time_skew_and_grace(bool historical, int age, int heartbeat, bool valid, bool stale)
    {
        const long now = 2_000_000;
        const long blockTime = 10_000;
        long reference = historical ? blockTime : now;
        IEthRpcModule eth = PriceModule((ulong)(reference - age));
        eth.eth_getHeaderByNumber(Arg.Any<BlockParameter>()).Returns(_ =>
            ResultWrapper<BlockHeaderForRpc?>.Success(new() { Number = 42, Timestamp = blockTime, Hash = TestItem.KeccakA }));
        TimeProvider time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(DateTimeOffset.FromUnixTimeSeconds(now));
        McpPriceReader reader = new(Profile(1), LimboLogs.Instance, time);

        bool found = reader.TryReadFeed(eth, new("TEST", TestItem.AddressA, heartbeat),
            historical ? new BlockParameter(42) : BlockParameter.Latest, null, out McpPriceQuote? quote, out _);

        Assert.That(found, Is.EqualTo(valid));
        if (!valid) return;
        Assert.That(quote!.AgeSeconds, Is.EqualTo(Math.Max(0, age)));
        Assert.That(quote.Stale, Is.EqualTo(stale));
    }

    [TestCase("3700000000000000", "0.0037")]
    [TestCase("1", "0.000000000000000001")]
    [TestCase("1000000000000000000", "1.00")]
    [TestCase("1234560000000000000", "1.23")]
    [TestCase("999999999999999999", "1.00")]
    [TestCase("-999999999999999999", "-1.00")]
    [TestCase("0", "0")]
    public void Usd_values_preserve_small_significant_amounts(string raw, string expected)
    {
        McpPriceQuote quote = new(new("TEST", TestItem.AddressA, 3600), 100_000_000, 8, 1, 1, 0, false);
        Assert.That(quote.ValueUsd(BigInteger.Parse(raw), 18), Is.EqualTo(expected));
    }

    [TestCase("token_balances")]
    [TestCase("fee_estimate")]
    [TestCase("explain_transaction")]
    [TestCase("address_activity")]
    public async Task Every_usd_value_identifies_its_price_source(string tool)
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: builder =>
            builder.AddScoped<IGenesisPostProcessor, McpTxTokensTests.FeedGenesis>());
        Transaction tx = await McpPriceIsolationTests.Deposit(node);
        await using McpClient client = await node.CreateClient();
        (string, object?)[] args = tool switch
        {
            "token_balances" => [("owner", TestItem.AddressB.ToString()), ("tokens", new[] { node.Chain.Container.Resolve<McpChainProfile>().WrappedNativeToken!.ToString() }), ("includeUsd", true)],
            "explain_transaction" => [("hash", tx.Hash!.ToString()), ("includeUsd", true)],
            "address_activity" => [("address", TestItem.AddressB.ToString()), ("includeUsd", true)],
            _ => []
        };

        CallToolResult call = await McpToolCalls.Call(client, tool, args);
        JsonElement result = McpAssert.Success(call);
        JsonElement price = tool switch
        {
            "token_balances" => result.GetProperty("tokens")[0],
            "fee_estimate" => result.GetProperty("transferCost"),
            "explain_transaction" => result.GetProperty("fees"),
            _ => result.GetProperty("movements")[0]
        };
        Assert.That(price.TryGetProperty("pricedVia", out JsonElement via) ? via.GetString() : null, Is.EqualTo("ETH/USD"));
        Assert.That(price.GetProperty("priceUpdatedAt").GetUInt64(), Is.GreaterThan(0));
        await McpToolCalls.AssertConformsToOutputSchema(client, tool, call);
    }

    [TestCase("address_activity", "current quote")]
    [TestCase("token_balances", "selected block")]
    public async Task Usd_parameter_describes_the_price_reference(string tool, string reference)
    {
        await using McpTestNode node = await McpTestNode.Create();
        await using McpClient client = await node.CreateClient();
        McpClientTool entry = (await client.ListToolsAsync()).Single(item => item.Name == tool);
        Assert.That(entry.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("includeUsd").GetProperty("description").GetString(),
            Does.Contain(reference));
    }

    [Test]
    public async Task Historical_balances_use_a_price_fresh_at_the_selected_block()
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: builder =>
            builder.AddScoped<IGenesisPostProcessor, HistoricalFeedGenesis>());
        await node.Seed();
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "token_balances",
            [("owner", TestItem.AddressB.ToString()), ("tokens", Array.Empty<string>()), ("block", "0"), ("includeUsd", true)]);

        JsonElement native = McpAssert.Success(call).GetProperty("native");
        Assert.That(native.TryGetProperty("valueUsd", out _), Is.True, "the quote was fresh at genesis, even though it is stale now");
        Assert.That(native.GetProperty("priceUpdatedAt").GetUInt64(), Is.EqualTo(node.Chain.BlockTree.Genesis!.Timestamp - 10));
        await McpToolCalls.AssertConformsToOutputSchema(client, "token_balances", call);
    }

    [TestCase("token_balances")]
    [TestCase("address_activity")]
    public async Task Missing_verified_token_decimals_omit_usd_with_a_note(string tool)
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: builder =>
            builder.AddScoped<IGenesisPostProcessor, TokensGenesis>());
        await Mint(node, Usdc);
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, tool, tool == "token_balances"
            ? [("owner", TestItem.AddressB.ToString()), ("tokens", new[] { Usdc.ToString() }), ("includeUsd", true)]
            : [("address", TestItem.AddressB.ToString()), ("token", Usdc.ToString()), ("includeUsd", true)]);

        JsonElement result = McpAssert.Success(call);
        JsonElement value = result.GetProperty(tool == "token_balances" ? "tokens" : "movements")[0];
        Assert.That(value.TryGetProperty("valueUsd", out _), Is.False);
        Assert.That(result.TryGetProperty("usdNotes", out JsonElement notes) ? notes.ToString() : "", Does.Contain("decimals"));
        await McpToolCalls.AssertConformsToOutputSchema(client, tool, call);
    }

    [Test]
    public async Task Fake_usdc_symbol_never_authorizes_a_price_in_any_tool()
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: builder =>
            builder.AddScoped<IGenesisPostProcessor, TokensGenesis>());
        await Mint(node, TestItem.AddressA);
        await using McpClient client = await node.CreateClient();
        CallToolResult balances = await McpToolCalls.Call(client, "token_balances",
            [("owner", TestItem.AddressB.ToString()), ("tokens", new[] { TestItem.AddressA.ToString() }), ("includeUsd", true)]);
        CallToolResult activity = await McpToolCalls.Call(client, "address_activity",
            [("address", TestItem.AddressB.ToString()), ("token", TestItem.AddressA.ToString()), ("includeUsd", true)]);
        CallToolResult price = await McpToolCalls.Call(client, "token_price", [("token", TestItem.AddressA.ToString())]);

        foreach (JsonElement entry in new[] { McpAssert.Success(balances).GetProperty("tokens")[0], McpAssert.Success(activity).GetProperty("movements")[0] })
        {
            Assert.That(entry.GetProperty("symbol").GetString(), Is.EqualTo("USDC"));
            Assert.That(entry.TryGetProperty("valueUsd", out _), Is.False);
        }
        McpAssert.Error(price, McpAssert.Unavailable);
        await McpToolCalls.AssertConformsToOutputSchema(client, "token_balances", balances);
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", activity);
    }

    [TestCase(1UL, "WETH", "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2", "ETH")]
    [TestCase(1UL, "WBTC", "0x2260FAC5E5542a773Aa44fBCfeDf7C193bc2C599", "BTC")]
    [TestCase(1UL, "USDC", "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", "USDC")]
    [TestCase(1UL, "USDT", "0xdAC17F958D2ee523a2206206994597C13D831ec7", "USDT")]
    [TestCase(1UL, "DAI", "0x6B175474E89094C44Da98b954EedeAC495271d0F", "DAI")]
    [TestCase(100UL, "WXDAI", "0xe91D153E0b41518A2Ce8Dd3D7944Fa863463a97d", "DAI")]
    [TestCase(100UL, "WETH", "0x6A023CCd1ff6F2045C3309768eAd9E68F978f6e1", "ETH")]
    [TestCase(100UL, "USDC", "0xDDAfbb505ad214D7b80b1f830fcCc89B60fb7A83", "USDC")]
    [TestCase(100UL, "GNO", "0x9C58BAcC331c9aa871AFD802DB6379a98e80CEdb", "GNO")]
    public void Real_feed_tables_resolve_symbols_and_only_their_verified_addresses(ulong chain, string symbol, string address, string pair)
    {
        McpChainProfile profile = Profile(chain);
        Assert.That(profile.Tokens.Single(token => token.Symbol == symbol).Address, Is.EqualTo(new Address(address)));
        Assert.That(profile.PriceFeed(symbol)?.Symbol, Is.EqualTo(pair));
        Assert.That(profile.PriceFeed(address), Is.SameAs(profile.PriceFeed(symbol)));
        Assert.That(profile.PriceFeed(TestItem.AddressA.ToString()), Is.Null);
    }

    [TestCase(1UL, "WBTC", "BTC/USD (WBTC assumed 1:1)")]
    [TestCase(100UL, "native", "DAI/USD (xDAI assumed 1:1)")]
    [TestCase(100UL, "GNO", "GNO/USD (bridged GNO assumed 1:1 GNO)")]
    public void Pegged_assets_state_the_underlying_feed(ulong chain, string asset, string expected)
    {
        IEthRpcModule eth = PriceModule((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10);
        McpPriceReader reader = new(Profile(chain), LimboLogs.Instance);
        Assert.That(reader.TryRead(eth, asset, BlockParameter.Latest, null, out McpPriceQuote? quote, out _), Is.True);
        Assert.That(quote!.PricedVia, Is.EqualTo(expected));
    }

    private static McpChainProfile Profile(ulong chain) => new(new ChainSpec(), new TestSpecProvider(Berlin.Instance) { ChainId = chain });

    private static IEthRpcModule PriceModule(ulong updated)
    {
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        byte[] round = new byte[160];
        ((UInt256)1).ToBigEndian(round.AsSpan(0, 32));
        ((UInt256)100_000_000).ToBigEndian(round.AsSpan(32, 32));
        ((UInt256)updated).ToBigEndian(round.AsSpan(96, 32));
        ((UInt256)1).ToBigEndian(round.AsSpan(128, 32));
        byte[] decimals = new byte[32];
        decimals[31] = 8;
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(),
            Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>()).Returns(info =>
                ResultWrapper<HexBytes>.Success(new HexBytes(((LegacyTransactionForRpc)info.ArgAt<SignableTransactionForRpc>(0)).Input![0] == 0xfe ? round : decimals)));
        return eth;
    }

    private static async Task<Transaction> Mint(McpTestNode node, Address token)
    {
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction tx = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce).WithGasPrice(1)
            .WithTo(token).WithData(McpAbiSignature.Parse("mint()").Selector).WithGasLimit(100_000)
            .SignedAndResolved(node.Chain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject;
        await node.Chain.AddBlock(tx);
        return tx;
    }

    private sealed class HistoricalFeedGenesis(IWorldState state, ISpecProvider specs) : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis)
        {
            state.CreateAccount(NativeFeed, UInt256.Zero);
            state.InsertCode(NativeFeed, TestContracts.Aggregator(350_000_000_000, genesis.Timestamp - 10), specs.GenesisSpec, isGenesis: true);
        }
    }

    private sealed class TokensGenesis(IWorldState state, ISpecProvider specs) : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis)
        {
            new McpTxTokensTests.FeedGenesis(state, specs).PostProcess(genesis);
            foreach ((Address address, bool decimals) in new[] { (Usdc, false), (TestItem.AddressA, true) })
            {
                byte[] owner = new byte[32];
                TestItem.AddressB.Bytes.CopyTo(owner.AsSpan(12));
                TestContracts.EvmAssembler asm = new TestContracts.EvmAssembler().Selector()
                    .JumpIfSelector("symbol()", "symbol")
                    .JumpIfSelector("balanceOf(address)", "balance")
                    .JumpIfSelector("mint()", "mint");
                if (decimals) asm.JumpIfSelector("decimals()", "decimals");
                asm.Push(0).Push(0).Op(Instruction.REVERT)
                    .Label("symbol").ReturnBlob(TestContracts.AbiString("USDC"))
                    .Label("balance").ReturnBlob(((UInt256)1_000_000).ToBigEndian())
                    .Label("mint").PushWord(((UInt256)1_000_000).ToBigEndian()).Push(0).Op(Instruction.MSTORE)
                    .PushWord(owner).PushWord(new byte[32]).PushWord(TestContracts.TransferTopic.BytesToArray())
                    .Push(32).Push(0).Op(Instruction.LOG3).Op(Instruction.STOP);
                if (decimals) asm.Label("decimals").ReturnBlob(((UInt256)6).ToBigEndian());
                if (!state.AccountExists(address)) state.CreateAccount(address, UInt256.Zero);
                state.InsertCode(address, asm.Build(), specs.GenesisSpec, isGenesis: true);
            }
        }
    }
}
