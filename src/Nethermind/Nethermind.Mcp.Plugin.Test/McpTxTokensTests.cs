// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>Token movement extraction, bounded ERC-1155 batch decoding and deadline-bound token metadata reads.</summary>
[Parallelizable(ParallelScope.Self)]
public class McpTxTokensTests
{
    [Test]
    public void Bounded_append_does_not_retain_movements_past_the_simulation_limit()
    {
        McpTokenMovement movement = new(TestItem.AddressA, "ERC-20", TestItem.AddressB, TestItem.AddressC, UInt256.One, null);
        List<McpTokenMovement> retained = Enumerable.Repeat(movement, 49).ToList();
        List<McpTokenMovement> decoded = Enumerable.Repeat(movement, 5_000).ToList();

        int omitted = McpTxTokens.AppendUpTo(retained, decoded, McpTransactionTools.MaxSimulateLogs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retained, Has.Count.EqualTo(McpTransactionTools.MaxSimulateLogs));
            Assert.That(omitted, Is.EqualTo(4_999));
        }
    }

    [Test]
    public void Count_movements_counts_a_large_batch_without_materializing_it()
    {
        McpTokenTally tally = new();

        int count = McpTxTokens.CountMovements(BatchLog(BatchData(5_000)).ToLogEntry(), null, tally);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(5_000));
            Assert.That(tally.Omitted, Is.Zero);
            Assert.That(tally.Undecodable, Is.Zero);
        }
    }

    private static readonly Hash256 TransferBatchTopic = Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])");

    private McpTestNode _node = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => _node = await McpTestNode.Create();

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_node is not null) await _node.DisposeAsync();
    }

    [Test]
    public void Block_receipt_stats_count_every_id_of_a_10000_id_batch()
    {
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        ReceiptForRpc[] receipts = [new() { Status = 1, GasUsed = 50_000, EffectiveGasPrice = 1, Logs = [BatchLog(BatchData(10_000))] }];
        eth.eth_getBlockReceipts(Arg.Any<BlockParameter>()).Returns(ResultWrapper<IEnumerable<ReceiptForRpc>?>.Success(receipts));
        List<string> notes = [];

        JsonObject stats = tools.ReceiptStats(eth, new BlockParameter(1), null, notes, static () => true, CancellationToken.None)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats["tokenTransfers"]!.GetValue<int>(), Is.EqualTo(10_000));
            Assert.That(stats["topTokens"]![0]!["transfers"]!.GetValue<int>(), Is.EqualTo(10_000));
        }
    }

    [Test]
    public void Block_receipt_stats_report_a_malformed_batch_as_undecodable()
    {
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        ReceiptForRpc[] receipts = [new() { Status = 1, GasUsed = 50_000, EffectiveGasPrice = 1, Logs = [BatchLog(BatchData(3, valuesCount: 2))] }];
        eth.eth_getBlockReceipts(Arg.Any<BlockParameter>()).Returns(ResultWrapper<IEnumerable<ReceiptForRpc>?>.Success(receipts));
        List<string> notes = [];

        JsonObject stats = tools.ReceiptStats(eth, new BlockParameter(1), null, notes, static () => true, CancellationToken.None)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats["tokenTransfers"]!.GetValue<int>(), Is.Zero);
            Assert.That(notes, Has.Some.Contains("could not be decoded"));
        }
    }

    [Test]
    public void Legacy_transfer_layout_is_reported_as_non_standard([Values] bool indexedFrom)
    {
        byte[] data = new byte[indexedFrom ? 64 : 96];
        if (!indexedFrom) TestItem.AddressA.Bytes.CopyTo(data.AsSpan(12));
        TestItem.AddressB.Bytes.CopyTo(data.AsSpan(indexedFrom ? 12 : 44));
        ((UInt256)42).ToBigEndian(data.AsSpan(data.Length - 32));
        LogEntryForRpc log = new()
        {
            Address = TestItem.AddressC,
            Topics = indexedFrom ? [McpKnownAbi.TransferTopic, Topic(TestItem.AddressA)] : [McpKnownAbi.TransferTopic],
            Data = data
        };
        List<McpTokenMovement> movements = [];
        McpTokenTally tally = new();
        Assert.That(McpTxTokens.Extract(log.ToLogEntry(), movements, null, tally), Is.Null);
        Assert.That(movements, Is.Empty, "an unsupported layout cannot establish whether 42 is an NFT id or a fungible amount");
        Assert.That(tally.Undecodable, Is.EqualTo(1));
        Assert.That(McpTxTokens.UndecodableNote(tally.Undecodable), Does.Contain("non-standard"));

        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_getBlockReceipts(Arg.Any<BlockParameter>()).Returns(ResultWrapper<IEnumerable<ReceiptForRpc>?>.Success(
            [new() { Status = 1, GasUsed = 50_000, EffectiveGasPrice = 1, Logs = [log] }]));
        List<string> notes = [];
        JsonObject stats = _node.Chain.Container.Resolve<McpTransactionTools>().ReceiptStats(eth,
            new BlockParameter(1), null, notes, static () => true, CancellationToken.None)!;
        Assert.That(stats["tokenTransfers"]!.GetValue<int>(), Is.Zero);
        Assert.That(notes, Has.Some.Contains("non-standard"));
    }

    [Test]
    public void Token_lookup_checks_the_deadline_before_every_metadata_call()
    {
        IEthRpcModule eth = SlowToken(TimeSpan.FromMilliseconds(200), out Func<int> calls);
        Stopwatch clock = Stopwatch.StartNew();

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(
            new McpTokenMetadata(), eth, [TestItem.AddressA], BlockParameter.Latest, 10, CancellationToken.None,
            () => clock.Elapsed > TimeSpan.FromMilliseconds(50));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls(), Is.EqualTo(1), "no further eth_call is started once the deadline passed");
            Assert.That(found, Is.Empty, "an interrupted lookup yields no metadata");
            Assert.That(skipped, Is.EqualTo(1), "and is reported as not looked up");
        }
    }

    [Test]
    public void Token_lookup_resolves_all_cached_tokens_before_spending_budget([Values] bool interrupted)
    {
        IEthRpcModule eth = SlowToken(TimeSpan.Zero, out _);
        McpTokenMetadata metadata = new();
        Address[] cached = [TestItem.AddressB, TestItem.AddressC, TestItem.AddressD];
        foreach (Address address in cached) Assert.That(metadata.Get(eth, address, BlockParameter.Latest), Is.Not.Null);
        bool spent = !interrupted;
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(_ => { spent = true; return ResultWrapper<HexBytes>.Success(new HexBytes(TestContracts.AbiString("MISS"))); });
        eth.ClearReceivedCalls();

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(metadata, eth,
            [TestItem.AddressA, .. cached], BlockParameter.Latest, 1, CancellationToken.None, () => spent);

        Assert.That(found.Keys.Select(static key => (Address)key), Is.EquivalentTo(cached), "cached tokens are independent of the RPC count and time budgets");
        Assert.That(skipped, Is.EqualTo(1));
        Assert.That(eth.ReceivedCalls().Count(), Is.EqualTo(interrupted ? 2 : 0), "only the uncached token may start RPC calls");
    }

    [Test]
    public void Token_lookup_ignores_the_head_cache_for_a_historical_block()
    {
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_getCode(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(static _ => ResultWrapper<byte[]>.Success([0x60]));
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(info =>
            {
                LegacyTransactionForRpc tx = (LegacyTransactionForRpc)info.ArgAt<SignableTransactionForRpc>(0);
                if (tx.Input![0] == 0x31) return ResultWrapper<HexBytes>.Success(new HexBytes(UInt256.One.ToBigEndian()));
                BlockParameter block = info.ArgAt<BlockParameter>(1);
                string symbol = block.Type == BlockParameterType.Latest ? "NEW" : "OLD";
                return ResultWrapper<HexBytes>.Success(new HexBytes(TestContracts.AbiString(symbol)));
            });
        McpTokenMetadata metadata = new();
        Assert.That(metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest)?.Symbol, Is.EqualTo("NEW"));
        eth.ClearReceivedCalls();

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(metadata, eth, [TestItem.AddressA],
            new BlockParameter(1), 1, CancellationToken.None);

        Assert.That(found[TestItem.AddressA].Symbol, Is.EqualTo("OLD"));
        Assert.That(skipped, Is.Zero);
        eth.Received(1).eth_getCode(TestItem.AddressA, Arg.Is<BlockParameter?>(block => block!.Type == BlockParameterType.BlockNumber));
    }

    [Test]
    public void Token_lookup_does_not_use_the_latest_cache_for_an_exact_current_head_selector()
    {
        BlockHeader oldHead = Build.A.BlockHeader.WithNumber(10).TestObject;
        BlockHeader newHead = Build.A.BlockHeader.WithNumber(11).TestObject;
        BlockHeader head = oldHead;
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(_ => new Block(head));
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_getCode(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(static _ => ResultWrapper<byte[]>.Success([0x60]));
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(info =>
            {
                LegacyTransactionForRpc tx = (LegacyTransactionForRpc)info.ArgAt<SignableTransactionForRpc>(0);
                if (tx.Input![0] == 0x31) return ResultWrapper<HexBytes>.Success(new HexBytes(UInt256.One.ToBigEndian()));
                BlockParameter block = info.ArgAt<BlockParameter>(1);
                string symbol = block.Type == BlockParameterType.Latest ? "LATEST" : "EXACT";
                return ResultWrapper<HexBytes>.Success(new HexBytes(TestContracts.AbiString(symbol)));
            });
        McpTokenMetadata metadata = new(LimboLogs.Instance, blockFinder);
        Assert.That(metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest)?.Symbol, Is.EqualTo("LATEST"));
        head = newHead;
        eth.ClearReceivedCalls();

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(metadata, eth, [TestItem.AddressA],
            new BlockParameter(newHead.Hash!), 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found[TestItem.AddressA].Symbol, Is.EqualTo("EXACT"));
            Assert.That(skipped, Is.Zero);
        }
        eth.Received(1).eth_getCode(TestItem.AddressA, Arg.Is<BlockParameter?>(block => block!.BlockHash == newHead.Hash));
    }

    [Test]
    public async Task Activity_metadata_note_distinguishes_budget_and_missing_decimals([Values] bool spent)
    {
        Address weth = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2");
        await using McpTestNode node = await McpTestNode.Create(c => c.ToolTimeout = 2000, b => b
            .AddScoped<IGenesisPostProcessor, FeedGenesis>()
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new McpPriceIsolationTests.PriceRpcProvider(inner)));
        await McpPriceIsolationTests.Deposit(node);
        McpTokenMetadata metadata = node.Chain.Container.Resolve<McpTokenMetadata>();
        if (!spent)
        {
            IEthRpcModule missing = SlowToken(TimeSpan.Zero, out _);
            missing.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
                .Returns(_ => ResultWrapper<HexBytes>.Success(new HexBytes([])));
            Assert.That(metadata.Get(missing, weth, BlockParameter.Latest), Is.Not.Null);
        }
        else ((McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeCall = (method, args) =>
        {
            if (method == nameof(IEthRpcModule.eth_getCode) && args[0] is Address address && address == weth) Thread.Sleep(1500);
        };
        await using McpClient client = await node.CreateClient();
        string block = node.Chain.BlockTree.Head!.Number.ToString();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity",
            [("address", TestItem.AddressB.ToString()), ("token", weth.ToString()), ("fromBlock", block), ("toBlock", block), ("includeUsd", true)]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("usdNotes").GetRawText(),
            Does.Contain(spent ? "metadata not read within the time budget" : "decimals() not available"));
        Assert.That(result.TryGetProperty("tokenMetadataOmitted", out _), Is.EqualTo(spent));
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Usd_metadata_note_distinguishes_transient_decimals_failures(
        [Values("token_balances", "address_activity")] string tool, [Values(ErrorCodes.Timeout, ErrorCodes.ResourceUnavailable, ErrorCodes.ExecutionReverted)] int failure)
    {
        Address weth = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2");
        await using McpTestNode node = await McpTestNode.Create(configureContainer: b => b
            .AddScoped<IGenesisPostProcessor, FeedGenesis>()
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        await McpPriceIsolationTests.Deposit(node);
        IEthRpcModule eth = SlowToken(TimeSpan.Zero, out _);
        eth.eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(Task.FromResult(ResultWrapper<UInt256?>.Success(UInt256.Zero)));
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(call =>
            {
                LegacyTransactionForRpc tx = (LegacyTransactionForRpc)call.ArgAt<SignableTransactionForRpc>(0);
                if (tx.To == weth && tx.Input![0] == 0x31) return ResultWrapper<HexBytes>.Fail("metadata read failed", failure);
                return ResultWrapper<HexBytes>.Success(new HexBytes(tx.Input![0] == 0x70 ? UInt256.One.ToBigEndian() : TestContracts.AbiString("WETH")));
            });
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).Override(nameof(IEthRpcModule.eth_call), eth);
        await using McpClient client = await node.CreateClient();
        (string, object?)[] args = tool == "token_balances"
            ? [("owner", TestItem.AddressB.ToString()), ("tokens", new[] { weth.ToString() }), ("includeUsd", true)]
            : [("address", TestItem.AddressB.ToString()), ("token", weth.ToString()), ("includeUsd", true)];
        CallToolResult result = await McpToolCalls.Call(client, tool, args);
        string notes = McpAssert.Success(result).GetProperty("usdNotes").GetRawText();
        Assert.That(notes, Does.Contain(failure == ErrorCodes.ExecutionReverted ? "decimals() not available" : "decimals() lookup failed; try again"));
        await McpToolCalls.AssertConformsToOutputSchema(client, tool, result);
    }

    [Test]
    public void Token_metadata_treats_missing_trie_node_as_a_transient_failure()
    {
        IEthRpcModule eth = SlowToken(TimeSpan.Zero, out _);
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(ResultWrapper<HexBytes>.Fail("missing trie node 0xab", ErrorCodes.Default));
        bool transient = false;

        byte[]? result = McpTokenMetadata.Call(eth, TestItem.AddressA, [0x31, 0x3c, 0xe5, 0x67], BlockParameter.Latest, 50_000, ref transient);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(transient, Is.True);
            Assert.That(McpTokenMetadata.MissingDecimalsNote(new McpTokenInfo(TestItem.AddressA, null, null, null) { DecimalsLookupFailed = transient }),
                Does.Contain("lookup failed; try again"));
        }
    }

    [Test]
    public void Token_metadata_serves_cached_entries_after_deadline_without_rpc([Values] bool cached)
    {
        IEthRpcModule eth = SlowToken(TimeSpan.Zero, out _);
        McpTokenMetadata metadata = new();
        McpTokenInfo? expected = cached ? metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest) : null;
        eth.ClearReceivedCalls();

        bool completed = metadata.TryGet(eth, TestItem.AddressA, BlockParameter.Latest, static () => true, out McpTokenInfo? info);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.EqualTo(cached));
            Assert.That(info, Is.SameAs(expected));
            Assert.That(eth.ReceivedCalls(), Is.Empty);
        }
    }

    [Test]
    public void Token_metadata_uses_node_chain_id_without_an_rpc()
    {
        IEthRpcModule eth = SlowToken(TimeSpan.Zero, out _);
        eth.eth_chainId().Returns(_ => throw new InvalidOperationException("unavailable"));
        McpTokenMetadata metadata = _node.Chain.Container.Resolve<McpTokenMetadata>();

        bool completed = metadata.TryGet(eth, TestItem.AddressA, BlockParameter.Latest, null, out McpTokenInfo? info);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.True);
            Assert.That(info?.Symbol, Is.EqualTo("SLOW"));
            IDictionary cache = (IDictionary)typeof(McpTokenMetadata).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadata)!;
            Assert.That(cache.Keys.Cast<(ulong Chain, AddressAsKey Address)>().Select(static key => key.Chain),
                Is.Not.Empty.And.All.EqualTo(_node.Chain.SpecProvider.ChainId), "DI must supply the node profile rather than using the unscoped chain-zero cache");
            eth.DidNotReceive().eth_chainId();
        }
    }

    [TestCase(123_456_789L, 30, 1UL, 8, true, false)]
    [TestCase(123_456_789L, 30, 1UL, 6, true, false)]
    [TestCase(123_456_789L, 3781, 1UL, 8, true, true)]
    [TestCase(0L, 30, 1UL, 8, false, false)]
    [TestCase(-1L, 30, 1UL, 8, false, false)]
    [TestCase(123_456_789L, 30, 0UL, 8, false, false)]
    public void Chainlink_round_requires_positive_answer_and_complete_freshness_data(
        long answer, int age, ulong answeredInRound, int feedDecimals, bool accepted, bool stale)
    {
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        ulong updatedAt = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (ulong)age;
        byte[] round = new byte[160];
        WriteWord(round, 0, 1);
        if (answer < 0) round.AsSpan(32, 32).Fill(0xff);
        else WriteWord(round, 32, (ulong)answer);
        WriteWord(round, 96, updatedAt);
        WriteWord(round, 128, answeredInRound);
        byte[] decimals = new byte[32];
        WriteWord(decimals, 0, (ulong)feedDecimals);
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(),
            Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(info => ResultWrapper<HexBytes>.Success(new HexBytes(
                ((LegacyTransactionForRpc)info.ArgAt<SignableTransactionForRpc>(0)).Input![0] == 0xfe ? round : decimals)));
        McpChainProfile profile = _node.Chain.Container.Resolve<McpChainProfile>();
        McpPriceReader reader = new(profile, LimboLogs.Instance);
        McpPriceFeed feed = new("TEST", TestItem.AddressA, 3600);

        bool found = reader.TryReadFeed(eth, feed, BlockParameter.Latest, null, out McpPriceQuote? quote, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.EqualTo(accepted));
            Assert.That(quote?.Stale, Is.EqualTo(accepted ? stale : null));
            if (accepted)
            {
                Assert.That(quote!.Decimals, Is.EqualTo(feedDecimals));
                Assert.That(quote.PriceUsd, Is.EqualTo(feedDecimals == 8 ? "1.23456789" : "123.456789"));
                Assert.That(quote.ValueUsd(UInt256.Parse("1000000000000000000"), 18),
                    Is.EqualTo(feedDecimals == 8 ? "1.23" : "123.46"));
            }
        }
    }

    private static void WriteWord(byte[] destination, int offset, ulong value) =>
        ((UInt256)value).ToBigEndian(destination.AsSpan(offset, 32));

    [Test]
    public async Task Token_price_returns_a_verified_feed_quote_matching_its_schema([Values] bool stale)
    {
        TimeProvider time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(_ => DateTimeOffset.UtcNow.AddHours(stale ? 2 : 0));
        await using McpTestNode node = await McpTestNode.Create(configureContainer: builder =>
            builder.AddSingleton(time).AddScoped<IGenesisPostProcessor, FeedGenesis>());
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "token_price", [("token", "native")]);
        JsonElement result = McpAssert.Success(call);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("priceUsd").GetString(), Is.EqualTo("3500"));
            Assert.That(result.GetProperty("stale").GetBoolean(), Is.EqualTo(stale));
            Assert.That(result.GetProperty("updatedAt").GetUInt64(), Is.GreaterThan(0));
        }

        await McpToolCalls.AssertConformsToOutputSchema(client, "token_price", call);
    }

    [Test]
    public async Task Fresh_native_price_is_added_to_balance_fee_estimate_and_explanation()
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: static builder =>
            builder.AddScoped<IGenesisPostProcessor, FeedGenesis>());
        SeededChain seeded = await node.Seed();
        await using McpClient client = await node.CreateClient();

        CallToolResult balances = await McpToolCalls.Call(client, "token_balances",
            [("owner", TestItem.AddressB.ToString()), ("tokens", new[] { "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2" }), ("includeUsd", true)]);
        CallToolResult fees = await McpToolCalls.Call(client, "fee_estimate", []);
        CallToolResult explained = await McpToolCalls.Call(client, "explain_transaction", [("hash", seeded.Transfer.Hash!.ToString()), ("includeUsd", true)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(McpAssert.Success(balances).GetProperty("native").GetProperty("valueUsd").GetString(), Is.Not.Null);
            Assert.That(McpAssert.Success(balances).GetProperty("native").GetProperty("priceUpdatedAt").GetUInt64(), Is.GreaterThan(0));
            Assert.That(McpAssert.Success(balances).GetProperty("tokens")[0].GetProperty("valueUsd").GetString(), Is.EqualTo("3500.00"));
            Assert.That(McpAssert.Success(fees).GetProperty("transferCost").GetProperty("valueUsd").GetString(), Is.Not.Null);
            Assert.That(McpAssert.Success(explained).GetProperty("fees").GetProperty("total").GetProperty("valueUsd").GetString(), Is.Not.Null);
        }

        await McpToolCalls.AssertConformsToOutputSchema(client, "token_balances", balances);
        await McpToolCalls.AssertConformsToOutputSchema(client, "fee_estimate", fees);
        await McpToolCalls.AssertConformsToOutputSchema(client, "explain_transaction", explained);
    }

    [Test]
    public async Task Address_activity_adds_usd_value_to_a_wrapped_native_deposit()
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: static builder =>
            builder.AddScoped<IGenesisPostProcessor, FeedGenesis>());
        PrivateKey sender = TestItem.PrivateKeyB;
        Address weth = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2");
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, sender.Address);
        Transaction deposit = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce).WithGasPrice(1)
            .WithTo(weth).WithData(McpAbiSignature.Parse("deposit()").Selector).WithGasLimit(100_000)
            .SignedAndResolved(node.Chain.EthereumEcdsa, sender).TestObject;
        Block block = await node.Chain.AddBlock(deposit);
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "address_activity",
            [("address", sender.Address.ToString()), ("token", weth.ToString()), ("fromBlock", block.Number.ToString()),
             ("toBlock", block.Number.ToString()), ("includeUsd", true)]);
        JsonElement activity = McpAssert.Success(call);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.GetProperty("movements").GetArrayLength(), Is.EqualTo(1));
            Assert.That(activity.GetProperty("movements")[0].GetProperty("direction").GetString(), Is.EqualTo("in"));
            Assert.That(activity.GetProperty("movements")[0].GetProperty("valueUsd").GetString(), Is.EqualTo("3500.00"));
            Assert.That(activity.GetProperty("pageNetFlows")[0].GetProperty("valueUsd").GetString(), Is.EqualTo("3500.00"));
        }

        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Address_activity_decodes_nfts_batches_and_self_transfers_once()
    {
        await using McpTestNode node = await McpTestNode.Create();
        PrivateKey sender = TestItem.PrivateKeyB;
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, sender.Address);
        Hash256 transfer = TestContracts.TransferTopic;
        Hash256 single = Keccak.Compute("TransferSingle(address,address,address,uint256,uint256)");
        byte[] singleData = new byte[64];
        WriteWord(singleData, 0, 7);
        WriteWord(singleData, 32, 3);
        byte[] batchData = BatchData(3);
        Hash256 tokenId = new(((UInt256)42).ToBigEndian());
        byte[] nftCode = Prepare.EvmCode.Log(0, 0, [tokenId, Topic(TestItem.AddressD), Topic(sender.Address), transfer]).Done;
        byte[] singleCode = Prepare.EvmCode.StoreDataInMemory(0, singleData)
            .Log(64, 0, [Topic(TestItem.AddressD), Topic(sender.Address), Topic(sender.Address), single]).Done;
        byte[] batchCode = Prepare.EvmCode.StoreDataInMemory(0, batchData)
            .Log(batchData.Length, 0, [Topic(TestItem.AddressD), Topic(sender.Address), Topic(sender.Address), TransferBatchTopic]).Done;
        byte[] selfData = new byte[32];
        WriteWord(selfData, 0, 5);
        byte[] selfCode = Prepare.EvmCode.StoreDataInMemory(0, selfData)
            .Log(32, 0, [Topic(sender.Address), Topic(sender.Address), transfer]).Done;
        byte[][] codes = [nftCode, singleCode, batchCode, selfCode];
        Transaction[] transactions = new Transaction[codes.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce + (ulong)i).WithGasPrice(1)
                .WithCode(codes[i]).WithGasLimit(500_000).SignedAndResolved(node.Chain.EthereumEcdsa, sender).TestObject;
        }

        Block block = await node.Chain.AddBlock(transactions);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity",
            [("address", sender.Address.ToString()), ("fromBlock", block.Number.ToString()), ("toBlock", block.Number.ToString()), ("limit", 10), ("order", "asc")]);
        JsonElement activity = McpAssert.Success(call);
        JsonElement movements = activity.GetProperty("movements");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(movements.GetArrayLength(), Is.EqualTo(6));
            Assert.That(movements[0].GetProperty("standard").GetString(), Is.EqualTo("ERC-721"));
            Assert.That(movements[1].GetProperty("standard").GetString(), Is.EqualTo("ERC-1155"));
            Assert.That(movements[2].GetProperty("standard").GetString(), Is.EqualTo("ERC-1155"));
            Assert.That(movements[5].GetProperty("direction").GetString(), Is.EqualTo("self"));
            Assert.That(activity.GetProperty("pageTransactions").GetInt32(), Is.EqualTo(4));
        }

        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public void Testnet_price_lookup_returns_no_value_without_an_rpc_call()
    {
        McpChainProfile profile = new(new ChainSpec(), new TestSpecProvider(Berlin.Instance) { ChainId = BlockchainIds.Sepolia });
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        McpPriceReader reader = new(profile, LimboLogs.Instance);

        bool found = reader.TryRead(eth, "native", BlockParameter.Latest, null, out McpPriceQuote? quote, out string reason);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.False);
            Assert.That(quote, Is.Null);
            Assert.That(reason, Does.Contain("testnets"));
            Assert.That(eth.ReceivedCalls(), Is.Empty);
        }
    }

    [Test]
    public void Token_without_a_verified_feed_returns_no_price()
    {
        McpPriceReader reader = _node.Chain.Container.Resolve<McpPriceReader>();
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();

        bool found = reader.TryRead(eth, TestItem.AddressA.ToString(), BlockParameter.Latest, null,
            out McpPriceQuote? quote, out string reason);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.False);
            Assert.That(quote, Is.Null);
            Assert.That(reason, Does.Contain("No verified USD price feed"));
            Assert.That(eth.ReceivedCalls(), Is.Empty);
        }
    }

    [Test]
    public async Task Optional_price_lookup_returns_before_a_stalled_feed_call_finishes()
    {
        using ManualResetEventSlim release = new();
        await using McpTestNode node = await McpTestNode.Create(
            configureContainer: static builder => builder.AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)),
            start: false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(),
            Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(_ =>
            {
                entered.TrySetResult();
                release.Wait();
                return ResultWrapper<HexBytes>.Success(new HexBytes(new byte[160]));
            });
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>())
            .Override(nameof(IEthRpcModule.eth_call), eth);
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        McpPriceReader reader = node.Chain.Container.Resolve<McpPriceReader>();
        Task<CallToolResult> call = executor.ExecuteLocalAsync("optional-price", async token =>
        {
            (McpPriceQuote? quote, string reason) = await reader.ReadOptionalAsync(executor, "native", BlockParameter.Latest,
                TimeSpan.FromMilliseconds(50), token);
            return executor.Success(new { price = quote?.PriceUsd, reason });
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CallToolResult result;
        try
        {
            result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }

        Assert.That(McpAssert.Success(result).GetProperty("reason").GetString(), Does.Contain("deadline"));
    }

    [TestCase(true, TestName = "Concurrent read: head tracked")]
    [TestCase(false, TestName = "Concurrent read: no block finder")]
    public void Concurrent_read_that_started_before_a_newer_one_does_not_overwrite_its_cache_entry(bool withBlockFinder)
    {
        BlockHeader oldHead = Build.A.BlockHeader.WithNumber(10).TestObject;
        BlockHeader newHead = Build.A.BlockHeader.WithNumber(11).TestObject;
        BlockHeader head = oldHead;
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(_ => new Block(head));
        McpTokenMetadata metadata = new(LimboLogs.Instance, withBlockFinder ? blockFinder : null);

        string symbol = "OLD";
        bool upgraded = false;
        int calls = 0;
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_chainId().Returns(ResultWrapper<ulong>.Success(1));
        eth.eth_getCode(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(static _ => ResultWrapper<byte[]>.Success([0x60]));
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(_ =>
            {
                string current = symbol;
                if (++calls == 3 && !upgraded)
                {
                    // During the first read's last call (decimals) the token is upgraded at a new head, and a second read completes first.
                    upgraded = true;
                    head = newHead;
                    symbol = "NEW";
                    Assert.That(metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest)?.Symbol, Is.EqualTo("NEW"));
                }

                return ResultWrapper<HexBytes>.Success(new HexBytes(TestContracts.AbiString(current)));
            });

        string? stale = metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest)?.Symbol;
        string? cached = metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest)?.Symbol;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stale, Is.EqualTo("OLD"), "the first read still returns what it read");
            Assert.That(cached, Is.EqualTo("NEW"), "but does not replace the newer cache entry");
            Assert.That(metadata.CachedCount, Is.EqualTo(1));
        }
    }

    [TestCase(10_000)]
    [TestCase(100_000)]
    public void Batch_counts_every_id_but_materializes_a_bounded_number_of_movements(int count)
    {
        List<McpTokenMovement> movements = [];
        McpTokenTally tally = new();

        McpDecodedLog? decoded = McpTxTokens.Extract(BatchLog(BatchData(count)).ToLogEntry(), movements, null, tally);
        JsonObject json = McpTxTokens.DecodedJson(decoded!);
        JsonNode ids = json["params"]![3]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(movements, Has.Count.EqualTo(McpTxTokens.MaxBatchMovements));
            Assert.That(movements.Count + tally.Omitted, Is.EqualTo(count), "every id is counted");
            Assert.That(tally.Undecodable, Is.Zero);
            Assert.That(movements[0].TokenId, Is.EqualTo((UInt256?)1));
            Assert.That(movements[^1].TokenId, Is.EqualTo((UInt256?)(ulong)McpTxTokens.MaxBatchMovements));
            Assert.That(movements[0].Amount, Is.EqualTo((UInt256)10));
            Assert.That(movements[0].From, Is.EqualTo(TestItem.AddressA));
            Assert.That(movements[0].To, Is.EqualTo(TestItem.AddressB));
            Assert.That(ids["value"]!.AsArray(), Has.Count.EqualTo(McpTxTokens.MaxBatchDisplayedEntries));
            Assert.That(ids["length"]!.GetValue<int>(), Is.EqualTo(count), "the decoded log discloses the full length");
            Assert.That(ids["entriesOmitted"]!.GetValue<int>(), Is.EqualTo(count - McpTxTokens.MaxBatchDisplayedEntries));
        }
    }

    [Test]
    public void Small_batch_decodes_like_the_generic_codec()
    {
        LogEntry log = BatchLog(BatchData(3)).ToLogEntry();
        List<McpTokenMovement> movements = [];

        McpDecodedLog? bounded = McpTxTokens.Extract(log, movements, null, new McpTokenTally());
        McpDecodedLog? generic = McpKnownAbi.TryDecodeLog(log);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(movements, Has.Count.EqualTo(3));
            Assert.That(McpTxTokens.DecodedJson(bounded!).ToJsonString(), Is.EqualTo(McpTxTokens.DecodedJson(generic!).ToJsonString()));
        }
    }

    private static IEnumerable<TestCaseData> MalformedBatches()
    {
        yield return new TestCaseData(BatchData(3, valuesCount: 2), 4).SetName("Malformed batch: length mismatch");
        byte[] outOfRange = BatchData(3);
        outOfRange[62] = 0x10; // values offset 4288, word-aligned but past the data
        yield return new TestCaseData(outOfRange, 4).SetName("Malformed batch: offset out of range");
        byte[] misaligned = BatchData(3);
        misaligned[31] = 65;
        yield return new TestCaseData(misaligned, 4).SetName("Malformed batch: misaligned offset");
        byte[] tooLong = BatchData(3);
        tooLong[95] = 200; // ids length beyond the data
        yield return new TestCaseData(tooLong, 4).SetName("Malformed batch: length beyond data");
        yield return new TestCaseData(new byte[40], 4).SetName("Malformed batch: short head");
        yield return new TestCaseData(BatchData(3), 3).SetName("Malformed batch: missing topic");
    }

    [TestCaseSource(nameof(MalformedBatches))]
    public void Malformed_batch_is_reported_as_undecodable(byte[] data, int topicCount)
    {
        LogEntryForRpc log = BatchLog(data);
        log.Topics = log.Topics![..topicCount];
        List<McpTokenMovement> movements = [];
        McpTokenTally tally = new();

        McpDecodedLog? decoded = McpTxTokens.Extract(log.ToLogEntry(), movements, null, tally);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.Null);
            Assert.That(movements, Is.Empty);
            Assert.That(tally.Undecodable, Is.EqualTo(1));
            Assert.That(McpTxTokens.UndecodableNote(tally.Undecodable), Does.Contain("could not be decoded"));
        }
    }

    [Test]
    public void Detached_token_lookup_returns_at_the_deadline_while_a_call_is_in_flight()
    {
        IEthRpcModule caller = Substitute.For<IEthRpcModule>();
        IEthRpcModule slow = SlowToken(TimeSpan.FromMilliseconds(1500), out Func<int> calls);
        IDisposable lease = Substitute.For<IDisposable>();
        List<Task> tracked = [];
        McpDetachedEth detached = new(() => Task.FromResult<(IEthRpcModule?, IDisposable?)>((slow, lease)), tracked.Add);
        Stopwatch clock = Stopwatch.StartNew();

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(
            new McpTokenMetadata(), caller, [TestItem.AddressA, TestItem.AddressB], BlockParameter.Latest, 10, CancellationToken.None,
            () => clock.Elapsed > TimeSpan.FromMilliseconds(100), detached);
        TimeSpan returnedAfter = clock.Elapsed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returnedAfter, Is.LessThan(TimeSpan.FromMilliseconds(1000)), "the caller stops waiting for the in-flight call");
            Assert.That(found, Is.Empty);
            Assert.That(skipped, Is.EqualTo(2));
            Assert.That(tracked, Has.Count.EqualTo(1), "the abandoned read is tracked");
            Assert.That(caller.ReceivedCalls(), Is.Empty, "the caller's module is not used while the worker may still hold its own");
        }

        Assert.That(tracked[0].Wait(TimeSpan.FromSeconds(10)), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls(), Is.EqualTo(1), "the worker starts no further call after the deadline");
            lease.Received(1).Dispose();
        }
    }

    [Test]
    public void Detached_token_lookup_finishes_normally_and_falls_back_to_the_callers_module()
    {
        IEthRpcModule fast = SlowToken(TimeSpan.Zero, out _);
        McpDetachedEth unavailable = new(() => Task.FromResult<(IEthRpcModule?, IDisposable?)>((null, null)), static _ => Assert.Fail("nothing is abandoned"));

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(
            new McpTokenMetadata(), fast, [TestItem.AddressA, TestItem.AddressB], BlockParameter.Latest, 10, CancellationToken.None,
            static () => false, unavailable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found.Keys.Select(static k => (Address)k), Is.EquivalentTo(new[] { TestItem.AddressA, TestItem.AddressB }));
            Assert.That(found[TestItem.AddressA].Symbol, Is.EqualTo("SLOW"));
            Assert.That(skipped, Is.Zero);
        }
    }

    // A token whose every eth_call takes `delay`; `calls` counts the eth_calls made.
    private static IEthRpcModule SlowToken(TimeSpan delay, out Func<int> calls)
    {
        int count = 0;
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_chainId().Returns(ResultWrapper<ulong>.Success(1));
        eth.eth_getCode(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(static _ => ResultWrapper<byte[]>.Success([0x60]));
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref count);
                Thread.Sleep(delay);
                return ResultWrapper<HexBytes>.Success(new HexBytes(TestContracts.AbiString("SLOW")));
            });
        calls = () => Volatile.Read(ref count);
        return eth;
    }

    private static LogEntryForRpc BatchLog(byte[] data) => new()
    {
        Address = TestItem.AddressC,
        Data = data,
        Topics = [TransferBatchTopic, Topic(TestItem.AddressA), Topic(TestItem.AddressA), Topic(TestItem.AddressB)]
    };

    // ABI-encodes (uint256[] ids, uint256[] values) with ids 1..count and every value 10, written directly so that
    // batches far above the codec's limits can be built.
    internal static byte[] BatchData(int count, int? valuesCount = null)
    {
        int values = valuesCount ?? count;
        byte[] data = new byte[32 * (4 + count + values)];
        Word(data, 0, 64);
        Word(data, 1, (ulong)(64 + 32 + 32 * count));
        Word(data, 2, (ulong)count);
        for (int i = 0; i < count; i++) Word(data, 3 + i, (ulong)(i + 1));
        Word(data, 3 + count, (ulong)values);
        for (int i = 0; i < values; i++) Word(data, 4 + count + i, 10);
        return data;

        static void Word(byte[] target, int index, ulong value) => ((UInt256)value).ToBigEndian(target.AsSpan(index * 32, 32));
    }

    private static Hash256 Topic(Address address)
    {
        byte[] word = new byte[32];
        address.Bytes.CopyTo(word.AsSpan(12));
        return new Hash256(word);
    }

    internal sealed class FeedGenesis(IWorldState state, ISpecProvider specs) : IGenesisPostProcessor
    {
        private static readonly Address Feed = new("0x5f4eC3Df9cbd43714FE2740f5E3616155c5b8419");
        private static readonly Address Weth = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2");

        public void PostProcess(Block genesis)
        {
            state.CreateAccount(Feed, UInt256.Zero);
            state.InsertCode(Feed, TestContracts.Aggregator(350_000_000_000, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10),
                specs.GenesisSpec, isGenesis: true);
            state.CreateAccount(Weth, UInt256.Zero);
            state.InsertCode(Weth, TestContracts.WrappedDeposit(TestItem.AddressB, 1_000_000_000_000_000_000),
                specs.GenesisSpec, isGenesis: true);
        }
    }
}
