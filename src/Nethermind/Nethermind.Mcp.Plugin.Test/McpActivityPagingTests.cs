// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Filters;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.Self)]
public class McpActivityPagingTests
{
    private static async Task<(McpTxScenario Scenario, Block Last, List<string> Hashes)> Seed(McpTestNode node, params int[] counts)
    {
        McpTxScenario scenario = await McpTxScenario.Create(node);
        List<string> hashes = [scenario.TokenCall.Hash!.ToString()];
        Block last = scenario.Block;
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(last.Header, TestItem.AddressB);
        foreach (int count in counts)
        {
            Transaction[] transactions = new Transaction[count];
            for (int i = 0; i < count; i++)
            {
                transactions[i] = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce++).WithGasPrice(1)
                    .WithTo(scenario.TokenContract).WithData(McpTxScenario.TransferCallData).WithGasLimit(100_000)
                    .SignedAndResolved(node.Chain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject;
                hashes.Add(transactions[i].Hash!.ToString());
            }
            last = await node.Chain.AddBlock(transactions);
        }
        return (scenario, last, hashes);
    }

    private static (string, object?)[] Query(McpTxScenario scenario, Block last, string order, int limit = 1) =>
        [("address", TestItem.AddressB.ToString()), ("token", scenario.TokenContract.ToString()),
            ("fromBlock", scenario.Block.Number.ToString()), ("toBlock", last.Number.ToString()), ("order", order), ("limit", limit)];

    [Test]
    public async Task Activity_recent_index_default_starts_in_covered_history()
    {
        ILogIndexStorage index = Substitute.For<ILogIndexStorage>();
        index.Enabled.Returns(true);
        await using McpTestNode node = await McpTestNode.Create(c => { c.MaxLogBlockRange = 2; c.MaxIndexedLogBlockRange = 100; },
            b => b.AddSingleton(index));
        (McpTxScenario scenario, Block last, _) = await Seed(node, 1, 1, 1, 1, 1, 1);
        index.MinBlockNumber.Returns((int)last.Number - 3);
        index.MaxBlockNumber.Returns((int)last.Number);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity",
            [("address", TestItem.AddressB.ToString()), ("token", scenario.TokenContract.ToString()), ("limit", 1)]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("movements")[0].GetProperty("blockNumber").GetUInt64(), Is.EqualTo(last.Number));
        Assert.That(result.GetProperty("scannedFrom").GetUInt64(), Is.GreaterThanOrEqualTo(last.Number - 3));
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_order_and_stream_cap_resume_are_exact([Values("asc", "desc")] string order, [Values] bool stream)
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: b => b.AddDecorator<IJsonRpcConfig>((_, rpc) =>
        {
            rpc.EnableLogsStreamMode = stream;
            rpc.MaxLogsPerResponse = 7;
            return rpc;
        }));
        (McpTxScenario scenario, Block last, List<string> expected) = await Seed(node, 15, 1);
        if (order == "desc") expected.Reverse();
        await using McpClient client = await node.CreateClient();
        Assert.That(await ReadActivityHashes(client, Query(scenario, last, order, 3)), Is.EqualTo(expected));
    }

    [Test]
    public async Task Activity_explicit_start_preserves_receipts_before_the_index([Values("asc", "desc")] string order)
    {
        ILogIndexStorage index = Substitute.For<ILogIndexStorage>();
        index.Enabled.Returns(true);
        await using McpTestNode node = await McpTestNode.Create(c => { c.MaxLogBlockRange = 2; c.MaxIndexedLogBlockRange = 100; },
            b => b.AddSingleton(index));
        (McpTxScenario scenario, Block last, List<string> expected) = await Seed(node, 1, 1, 1, 1, 1, 1);
        index.MinBlockNumber.Returns((int)last.Number - 2);
        index.MaxBlockNumber.Returns((int)last.Number);
        if (order == "desc") expected.Reverse();
        await using McpClient client = await node.CreateClient();

        List<string> actual = await ReadActivityHashes(client, Query(scenario, last, order, 50), rejectClamp: true);

        Assert.That(actual, Is.EqualTo(expected));
    }

    private static async Task<List<string>> ReadActivityHashes(McpClient client, (string, object?)[] query, bool rejectClamp = false)
    {
        List<string> actual = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            List<(string, object?)> args = [.. query];
            if (cursor is not null) args.Add(("cursor", cursor));
            CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
            JsonElement result = McpAssert.Success(call);
            if (rejectClamp)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result.TryGetProperty("clampedFromBlock", out _), Is.False, "indexed coverage is not the receipt history floor");
                    Assert.That(result.GetProperty("note").GetString(), Does.Not.Contain("outside this query window"));
                }
            }
            actual.AddRange(result.GetProperty("movements").EnumerateArray().Select(static m => m.GetProperty("transactionHash").GetString()!));
            cursor = result.GetProperty("truncated").GetBoolean() ? result.GetProperty("nextCursor").GetString() : null;
            await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
            Assert.That(++pages, Is.LessThan(30));
        } while (cursor is not null);
        return actual;
    }

    private static async Task<McpTestNode> InstrumentedActivityNode(ILogIndexStorage index, bool stream = false, int? cap = null) =>
        await McpTestNode.Create(c => c.ToolTimeout = 3000, b => b.AddSingleton(index)
            .AddScoped<IGenesisPostProcessor, ActivityGenesis>()
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new McpPriceIsolationTests.PriceRpcProvider(inner))
            .AddDecorator<IJsonRpcConfig>((_, rpc) =>
            {
                rpc.EnableLogsStreamMode = stream;
                rpc.MaxLogsPerResponse = cap ?? (stream ? 128 : 20_000);
                return rpc;
            }));

    private sealed class ActivityGenesis : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis) => genesis.Header.GasLimit = 30_000_000;
    }

    private static ILogIndexStorage ActivityIndex()
    {
        ILogIndexStorage index = Substitute.For<ILogIndexStorage>();
        index.Enabled.Returns(true);
        index.MinBlockNumber.Returns(1);
        return index;
    }

    private static List<string> ReceiptHashes(McpTestNode node, Address token) => Enumerable.Range(1, (int)node.Chain.BlockTree.Head!.Number)
        .SelectMany(number => node.Chain.ReceiptStorage.Get(node.Chain.BlockTree.FindBlock((ulong)number, BlockTreeLookupOptions.RequireCanonical)!))
        .Where(receipt => receipt.Logs!.Any(log => log.Address == token))
        .Select(static receipt => receipt.TxHash!.ToString()).Reverse().ToList();

    [Test]
    public async Task Activity_descending_dense_older_range_finishes_in_five_pages([Values] bool stream)
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await InstrumentedActivityNode(index, stream);
        (McpTxScenario scenario, Block last, _) = await Seed(node, [70, 70, .. new int[200]]);
        index.MaxBlockNumber.Returns((int)last.Number);
        McpPriceIsolationTests.PriceRpcProvider provider = (McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.BeforeCall = (method, _) => { if (method == nameof(IEthRpcModule.eth_getLogs)) Thread.Sleep(40); };
        await using McpClient client = await node.CreateClient();
        List<string> expected = ReceiptHashes(node, scenario.TokenContract);
        List<string> actual = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            List<(string, object?)> args = [.. Query(scenario, last, "desc", 50)];
            if (cursor is not null) args.Add(("cursor", cursor));
            CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
            JsonElement result = McpAssert.Success(call);
            actual.AddRange(result.GetProperty("movements").EnumerateArray().Select(static item => item.GetProperty("transactionHash").GetString()!));
            cursor = result.GetProperty("truncated").GetBoolean() ? result.GetProperty("nextCursor").GetString() : null;
            await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
            Assert.That(++pages, Is.LessThanOrEqualTo(5), "a capped older range must not become one RPC per empty block");
        } while (cursor is not null);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public async Task Activity_span_recovers_after_a_dense_block([Values("asc", "desc")] string order)
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await InstrumentedActivityNode(index, cap: 7);
        (McpTxScenario scenario, Block dense, _) = await Seed(node, order == "desc" ? [.. new int[80], 140] : [140, .. new int[80]]);
        index.MaxBlockNumber.Returns((int)dense.Number);
        List<ulong> olderSpans = [];
        McpPriceIsolationTests.PriceRpcProvider provider = (McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.BeforeCall = (method, args) =>
        {
            if (method != nameof(IEthRpcModule.eth_getLogs)) return;
            Filter filter = (Filter)args[0]!;
            if (order == "desc" ? filter.ToBlock.BlockNumber < dense.Number : filter.FromBlock.BlockNumber > scenario.Block.Number + 1)
                olderSpans.Add(filter.ToBlock.BlockNumber!.Value - filter.FromBlock.BlockNumber!.Value + 1);
        };
        await using McpClient client = await node.CreateClient();

        List<string> actual = await ReadActivityHashes(client, Query(scenario, dense, order, 50));

        using (Assert.EnterMultipleScope())
        {
            List<string> expected = ReceiptHashes(node, scenario.TokenContract);
            if (order == "asc") expected.Reverse();
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(olderSpans, Has.Some.GreaterThan(1UL), "draining a dense block must restore wider scans");
        }
    }

    [Test]
    public async Task Activity_descending_exactly_128_logs_does_not_enter_block_mode()
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await InstrumentedActivityNode(index);
        (McpTxScenario scenario, Block last, _) = await Seed(node, 127, 0);
        index.MaxBlockNumber.Returns((int)last.Number);
        int receiptReads = 0;
        McpPriceIsolationTests.PriceRpcProvider provider = (McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.BeforeCall = (method, _) => { if (method == nameof(IEthRpcModule.eth_getBlockReceipts)) receiptReads++; };
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc", 50));

        Assert.That(McpAssert.Success(call).GetProperty("movements").GetArrayLength(), Is.EqualTo(50));
        Assert.That(receiptReads, Is.Zero, "exactly the read limit is a complete result, not evidence of a cap");
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_descending_stop_keeps_the_current_span()
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await InstrumentedActivityNode(index);
        (McpTxScenario scenario, Block last, _) = await Seed(node, 1, 0, 0, 0, 0, 0);
        index.MaxBlockNumber.Returns((int)last.Number);
        McpPriceIsolationTests.PriceRpcProvider provider = (McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        int reads = 0;
        provider.BeforeCall = (method, _) =>
        {
            if (method == nameof(IEthRpcModule.eth_getLogs) && Interlocked.Increment(ref reads) == 1) Thread.Sleep(1900);
        };
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc", 50));
        JsonElement result = McpAssert.Success(call);
        Assert.That(McpActivityCursor.TryDecode(result.GetProperty("nextCursor").GetString()!, out McpActivityCursor? state, out _), Is.True);
        Assert.That(state!.Positions.Select(static position => position.Span),
            Is.All.EqualTo(last.Number - scenario.Block.Number + 1), "stopping during a read must preserve the span for resume");
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
        List<(string, object?)> resumed = [.. Query(scenario, last, "desc", 50), ("cursor", result.GetProperty("nextCursor").GetString())];
        JsonElement next = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", [.. resumed]));
        Assert.That(next.GetProperty("movements").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task Activity_lagging_index_preserves_the_default_horizon([Values(1, 64, 65, 100)] int lag)
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await McpTestNode.Create(c => { c.MaxLogBlockRange = 2; c.MaxIndexedLogBlockRange = 1000; },
            b => b.AddSingleton(index));
        (McpTxScenario scenario, Block last, _) = await Seed(node, new int[110]);
        index.MaxBlockNumber.Returns((int)last.Number - lag);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity",
            [("address", TestItem.AddressB.ToString()), ("token", scenario.TokenContract.ToString())]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("movements").GetArrayLength(), Is.EqualTo(lag <= 64 ? 1 : 0));
        Assert.That(result.GetProperty("indexed").GetBoolean(), Is.False, "the unindexed head tail is part of this page");
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_quiet_windows_continue_within_the_same_call()
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await McpTestNode.Create(c => { c.MaxLogBlockRange = 2; c.MaxIndexedLogBlockRange = 1000; },
            b => b.AddSingleton(index));
        (McpTxScenario scenario, Block last, _) = await Seed(node, new int[110]);
        index.MaxBlockNumber.Returns((int)last.Number);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc", 50));
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("movements").GetArrayLength(), Is.EqualTo(1), "empty windows must widen during the call");
        Assert.That(result.GetProperty("truncated").GetBoolean(), Is.False);
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_quiet_address_three_hundred_thousand_blocks_back_is_found_in_two_calls()
    {
        ILogIndexStorage index = ActivityIndex();
        index.MaxBlockNumber.Returns(400_000);
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        Block head = Build.A.Block.WithNumber(400_000).TestObject;
        finder.Head.Returns(head);
        finder.FindHeader(400_000, Arg.Any<BlockTreeLookupOptions>()).Returns(head.Header);
        await using McpTestNode node = await McpTestNode.Create(configureContainer: b => b.AddSingleton(index).AddSingleton(finder)
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        Hash256 ownerTopic = new(new byte[12].Concat(TestItem.AddressB.Bytes.ToArray()).ToArray());
        FilterLog log = new(0, 100_000, 1, TestItem.KeccakA, 0, TestItem.KeccakB, TestItem.AddressC,
            new byte[32], [TestContracts.TransferTopic, new Hash256(new byte[32]), ownerTopic]);
        int reads = 0;
        eth.eth_getLogs(Arg.Any<Filter>()).Returns(call =>
        {
            reads++;
            Filter filter = call.Arg<Filter>();
            return ResultWrapper<IEnumerable<FilterLog>>.Success(filter.FromBlock.BlockNumber <= log.BlockNumber
                && filter.ToBlock.BlockNumber >= log.BlockNumber ? [log] : []);
        });
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).Override(nameof(IEthRpcModule.eth_getLogs), eth);
        await using McpClient client = await node.CreateClient();
        string? cursor = null;
        int found = 0;
        for (int page = 0; page < 2 && found == 0; page++)
        {
            List<(string, object?)> args = [("address", TestItem.AddressB.ToString()), ("token", TestItem.AddressC.ToString()), ("toBlock", "400000")];
            if (cursor is not null) args.Add(("cursor", cursor));
            CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
            JsonElement result = McpAssert.Success(call);
            found += result.GetProperty("movements").GetArrayLength();
            cursor = result.TryGetProperty("nextCursor", out JsonElement next) ? next.GetString() : null;
            await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
        }
        Assert.That(found, Is.EqualTo(1), "300k empty blocks must be skipped with wider windows, not separate pages");
        Assert.That(reads, Is.LessThanOrEqualTo(80));
    }

    [Test]
    public async Task Activity_descending_reports_the_whole_covered_span()
    {
        await using McpTestNode node = await McpTestNode.Create();
        (McpTxScenario scenario, Block last, _) = await Seed(node, 0, 1, 0);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc", 50));
        JsonElement result = McpAssert.Success(call);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("fromBlock").GetUInt64(), Is.EqualTo(scenario.Block.Number));
            Assert.That(result.GetProperty("toBlock").GetUInt64(), Is.EqualTo(last.Number));
            Assert.That(result.GetProperty("coveredTo").GetUInt64(), Is.EqualTo(scenario.Block.Number));
        }
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_stop_before_merged_progress_has_no_coverage()
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await InstrumentedActivityNode(index);
        (McpTxScenario scenario, Block last, _) = await Seed(node, 1);
        index.MaxBlockNumber.Returns((int)last.Number);
        McpPriceIsolationTests.PriceRpcProvider provider = (McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.BeforeCall = (method, _) => { if (method == nameof(IEthRpcModule.eth_getLogs)) Thread.Sleep(1900); };
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc", 50));
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("coveredTo").ValueKind, Is.EqualTo(JsonValueKind.Null), "no merged log or complete window was read");
        Assert.That(result.GetProperty("truncationReason").GetString(), Is.EqualTo("time_budget"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_limit_reason_survives_a_simultaneous_clock_cut()
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.ToolTimeout = 3000,
            b => b.AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        (McpTxScenario scenario, Block last, _) = await Seed(node, 0);
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        static Hash256 Topic(Address address) => new(new byte[12].Concat(address.Bytes.ToArray()).ToArray());
        byte[] data = new byte[64];
        data[31] = data[63] = 1;
        FilterLog log = new(0, last.Number, last.Timestamp, last.Hash!, 0, TestItem.KeccakB, scenario.TokenContract, data,
            [Keccak.Compute("TransferSingle(address,address,address,uint256,uint256)"), Topic(TestItem.AddressD), Topic(TestItem.AddressC), Topic(TestItem.AddressB)]);
        int reads = 0;
        eth.eth_getLogs(Arg.Any<Filter>()).Returns(_ =>
        {
            if (++reads != 4) return ResultWrapper<IEnumerable<FilterLog>>.Success([]);
            Thread.Sleep(1900);
            return ResultWrapper<IEnumerable<FilterLog>>.Success([log, new FilterLog(1, log.BlockNumber, log.BlockTimestamp, log.BlockHash,
                0, log.TransactionHash, log.Address, log.Data, log.Topics)]);
        });
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).Override(nameof(IEthRpcModule.eth_getLogs), eth);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "asc", 1));
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("movements").GetArrayLength(), Is.EqualTo(1));
        Assert.That(result.GetProperty("truncationReason").GetString(), Is.EqualTo("limit"), "the returned page reached its requested count");
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_cursor_pins_moving_tags([Values("safe", "finalized")] string tag)
    {
        await using McpTestNode node = await McpTestNode.Create();
        (McpTxScenario scenario, Block last, _) = await Seed(node, 2);
        node.Chain.BlockTree.ForkChoiceUpdated(last.Hash, last.Hash);
        await using McpClient client = await node.CreateClient();
        List<(string, object?)> args = [.. Query(scenario, last, "desc").Where(static arg => arg.Item1 != "toBlock"), ("toBlock", tag)];
        JsonElement first = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", [.. args]));
        Block advanced = await node.Chain.AddBlock();
        node.Chain.BlockTree.ForkChoiceUpdated(advanced.Hash, advanced.Hash);
        args.Add(("cursor", first.GetProperty("nextCursor").GetString()));
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
        Assert.That(call.IsError, Is.Not.True, "moving tags must retain the cursor's resolved block");
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_covered_to_stops_at_the_limit_cut()
    {
        await using McpTestNode node = await McpTestNode.Create();
        (McpTxScenario scenario, Block last, _) = await Seed(node, 2, 1);
        await using McpClient client = await node.CreateClient();
        JsonElement result = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "asc", 1)));
        Assert.That(result.GetProperty("toBlock").GetUInt64(), Is.LessThan(last.Number));
        Assert.That(result.GetProperty("coveredTo").GetUInt64(), Is.EqualTo(result.GetProperty("toBlock").GetUInt64()));
    }

    [Test]
    public async Task Activity_soft_clock_returns_a_resumable_partial_page()
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.ToolTimeout = 1000,
            b => b.AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        (McpTxScenario scenario, Block last, _) = await Seed(node, 1);
        IEthRpcModule slow = Substitute.For<IEthRpcModule>();
        int calls = 0;
        slow.eth_getLogs(Arg.Any<Filter>()).Returns(_ =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(350);
            return ResultWrapper<IEnumerable<FilterLog>>.Success([]);
        });
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).Override(nameof(IEthRpcModule.eth_getLogs), slow);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "asc"));
        Assert.That(call.IsError, Is.Not.True, "optional scan work must stop before the executor timeout");
        JsonElement page = McpAssert.Success(call);
        Assert.That(page.GetProperty("truncated").GetBoolean(), Is.True);
        Assert.That(calls, Is.LessThanOrEqualTo(2));
        CallToolResult resumed = await McpToolCalls.Call(client, "address_activity", [.. Query(scenario, last, "asc"), ("cursor", page.GetProperty("nextCursor").GetString())]);
        Assert.That(resumed.IsError, Is.Not.True);
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }
    [Test]
    public async Task Activity_slow_completed_reads_eventually_finish([Values] bool capped)
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await InstrumentedActivityNode(index, cap: capped ? 1 : 20_000);
        (McpTxScenario scenario, Block last, _) = await Seed(node, 1, 0, 0);
        index.MaxBlockNumber.Returns((int)last.Number);
        McpPriceIsolationTests.PriceRpcProvider provider = (McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.BeforeCall = (method, _) => { if (method == nameof(IEthRpcModule.eth_getLogs)) Thread.Sleep(1900); };
        await using McpClient client = await node.CreateClient();
        List<string> actual = [];
        string? cursor = null;
        for (int calls = 0; calls < 8; calls++)
        {
            List<(string, object?)> args = [.. Query(scenario, last, "desc", 50)];
            if (cursor is not null) args.Add(("cursor", cursor));
            CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
            JsonElement page = McpAssert.Success(call);
            actual.AddRange(page.GetProperty("movements").EnumerateArray().Select(static m => m.GetProperty("transactionHash").GetString()!));
            string? next = page.TryGetProperty("nextCursor", out JsonElement value) ? value.GetString() : null;
            if (next is not null) Assert.That(next, Is.Not.EqualTo(cursor), "a completed slow read must advance the cursor");
            cursor = next;
            await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
            if (cursor is null) break;
        }
        Assert.That(cursor, Is.Null, "slow reads must terminate within eight calls");
        Assert.That(actual, Is.EqualTo(ReceiptHashes(node, scenario.TokenContract)));
    }

    [Test]
    public async Task Activity_later_window_failure_preserves_earlier_movements([Values] bool missingReceipts)
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.MaxLogBlockRange = 1,
            b => b.AddDecorator<IRpcModuleProvider>(static (_, inner) => new McpPriceIsolationTests.PriceRpcProvider(inner)));
        (McpTxScenario scenario, Block last, _) = await Seed(node, 1, 1);
        McpPriceIsolationTests.PriceRpcProvider provider = (McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        Block failing = node.Chain.BlockTree.FindBlock(last.Number - 1, BlockTreeLookupOptions.RequireCanonical)!;
        TxReceipt[] receipts = node.Chain.ReceiptStorage.Get(failing);
        if (missingReceipts) node.Chain.ReceiptStorage.RemoveReceipts(failing);
        else provider.BeforeCall = (method, args) =>
        {
            if (method == nameof(IEthRpcModule.eth_getLogs) && ((Filter)args[0]!).ToBlock.BlockNumber == failing.Number)
                throw new InvalidOperationException("private scan details must never be returned");
        };
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc", 50));
        Assert.That(call.IsError, Is.Not.True, "a later error must not discard the first window");
        JsonElement page = McpAssert.Success(call);
        Assert.That(page.GetProperty("movements").GetArrayLength(), Is.EqualTo(1));
        Assert.That(page.GetProperty("note").GetString(), Does.Contain($"block {failing.Number}").And.Not.Contain("private scan details"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
        provider.BeforeCall = null;
        if (missingReceipts) node.Chain.ReceiptStorage.Insert(failing, receipts);
        List<(string, object?)> resumed = [.. Query(scenario, last, "desc", 50), ("cursor", page.GetProperty("nextCursor").GetString())];
        JsonElement rest = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", [.. resumed]));
        Assert.That(rest.GetProperty("movements")[0].GetProperty("transactionHash").GetString(), Is.EqualTo(receipts.Single(receipt => receipt.Logs!.Any(log => log.Address == scenario.TokenContract)).TxHash!.ToString()));
    }

    [Test]
    public async Task Activity_indexed_flag_uses_coverage_bounds([Values] bool covered)
    {
        ILogIndexStorage index = ActivityIndex();
        await using McpTestNode node = await InstrumentedActivityNode(index);
        (McpTxScenario scenario, Block last, _) = await Seed(node, 1);
        index.MinBlockNumber.Returns((int)scenario.Block.Number);
        index.MaxBlockNumber.Returns((int)last.Number - (covered ? 0 : 1));
        await using McpClient client = await node.CreateClient();
        JsonElement result = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc", 50)));
        Assert.That(result.GetProperty("indexed").GetBoolean(), Is.EqualTo(covered));
    }

    [Test]
    public async Task Activity_resumed_coverage_does_not_repeat_consumed_blocks([Values("asc", "desc")] string order)
    {
        await using McpTestNode node = await McpTestNode.Create();
        (McpTxScenario scenario, Block last, _) = await Seed(node, 0, 1, 0);
        await using McpClient client = await node.CreateClient();
        JsonElement first = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", Query(scenario, last, order)));
        JsonElement second = McpAssert.Success(await McpToolCalls.Call(client, "address_activity",
            [.. Query(scenario, last, order), ("cursor", first.GetProperty("nextCursor").GetString())]));
        ulong original = order == "asc" ? scenario.Block.Number : last.Number;
        ulong boundary = second.GetProperty(order == "asc" ? "fromBlock" : "toBlock").GetUInt64();
        Assert.That(boundary, order == "asc" ? Is.GreaterThan(original) : Is.LessThan(original), "coverage belongs only to this call");
        JsonElement final = second.GetProperty("truncated").GetBoolean()
            ? McpAssert.Success(await McpToolCalls.Call(client, "address_activity",
                [.. Query(scenario, last, order), ("cursor", second.GetProperty("nextCursor").GetString())])) : second;
        Assert.That(final.GetProperty("note").GetString(), Does.Not.Contain("No activity"), "earlier pages contained activity");
    }

    [Test]
    public async Task Activity_cursor_rejects_a_different_tag_type([Values("latest", "safe", "finalized")] string changed)
    {
        await using McpTestNode node = await McpTestNode.Create();
        (McpTxScenario scenario, Block last, _) = await Seed(node, 2);
        node.Chain.BlockTree.ForkChoiceUpdated(last.Hash, last.Hash);
        await using McpClient client = await node.CreateClient();
        JsonElement first = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc")));
        List<(string, object?)> args = [.. Query(scenario, last, "desc").Where(static arg => arg.Item1 != "toBlock"),
            ("toBlock", changed), ("cursor", first.GetProperty("nextCursor").GetString())];
        McpAssert.Error(await McpToolCalls.Call(client, "address_activity", [.. args]), "invalid_input");
    }

    [Test]
    public async Task Activity_clamp_note_survives_resume()
    {
        await using McpTestNode node = await McpTestNode.Create();
        (McpTxScenario scenario, Block last, _) = await Seed(node, 1, 1);
        for (ulong number = 1; number <= scenario.Block.Number; number++)
            node.Chain.ReceiptStorage.RemoveReceipts(node.Chain.BlockTree.FindBlock(number, BlockTreeLookupOptions.RequireCanonical)!);
        await using McpClient client = await node.CreateClient();
        JsonElement first = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", Query(scenario, last, "desc")));
        JsonElement second = McpAssert.Success(await McpToolCalls.Call(client, "address_activity",
            [.. Query(scenario, last, "desc"), ("cursor", first.GetProperty("nextCursor").GetString())]));
        Assert.That(second.TryGetProperty("clampedFromBlock", out _), Is.True, "clamping applies to every page");
        Assert.That(second.GetProperty("note").GetString(), Does.Contain("receipt history"));
    }

}
