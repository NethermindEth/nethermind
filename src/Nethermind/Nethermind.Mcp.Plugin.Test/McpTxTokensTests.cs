// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json.Nodes;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>Token movement extraction, bounded ERC-1155 batch decoding and deadline-bound token metadata reads.</summary>
[Parallelizable(ParallelScope.Self)]
public class McpTxTokensTests
{
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
        eth.eth_getBlockReceipts(Arg.Any<BlockParameter>()).Returns(ResultWrapper<ReceiptForRpc[]?>.Success(receipts));
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
        eth.eth_getBlockReceipts(Arg.Any<BlockParameter>()).Returns(ResultWrapper<ReceiptForRpc[]?>.Success(receipts));
        List<string> notes = [];

        JsonObject stats = tools.ReceiptStats(eth, new BlockParameter(1), null, notes, static () => true, CancellationToken.None)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats["tokenTransfers"]!.GetValue<int>(), Is.Zero);
            Assert.That(notes, Has.Some.Contains("could not be decoded"));
        }
    }

    [Test]
    public void Token_lookup_checks_the_deadline_before_every_metadata_call()
    {
        IEthRpcModule eth = SlowToken(TimeSpan.FromMilliseconds(200), out Func<int> calls);
        Stopwatch clock = Stopwatch.StartNew();

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(
            new McpTokenMetadata(), eth, [TestItem.AddressA], 10, CancellationToken.None, () => clock.Elapsed > TimeSpan.FromMilliseconds(50));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls(), Is.EqualTo(1), "no further eth_call is started once the deadline passed");
            Assert.That(found, Is.Empty, "an interrupted lookup yields no metadata");
            Assert.That(skipped, Is.EqualTo(1), "and is reported as not looked up");
        }
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
            new McpTokenMetadata(), caller, [TestItem.AddressA, TestItem.AddressB], 10, CancellationToken.None,
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
            new McpTokenMetadata(), fast, [TestItem.AddressA, TestItem.AddressB], 10, CancellationToken.None, static () => false, unavailable);

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
}
