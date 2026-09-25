// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db.LogIndex;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// <c>get_logs</c> cursor paging over many seeded logs: pages never lose or repeat a log, cursors are bound to their
/// filter and cannot be edited, and each page scans a bounded block span unless the log index covers it.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class McpLogPagingTests
{
    private const int MaxLogBlockRange = 3;
    private const int MaxLogs = 50;
    private const int MaxResultSize = 12_000;
    private const int LogsPerTransaction = 5;

    /// <summary>Transactions per seeded block; empty blocks exercise pages that match nothing.</summary>
    private static readonly int[] TransactionsPerBlock = [3, 0, 2, 1, 0, 0, 4, 3, 0, 2];

    private static readonly Hash256 PagingTopic = Keccak.Compute("Nethermind.Mcp.PagingTopic");
    private static readonly Hash256 OtherTopic = Keccak.Compute("Nethermind.Mcp.OtherTopic");

    private McpTestNode _node = null!;
    private McpClient _client = null!;
    private ILogIndexStorage _logIndex = null!;
    private SeededLogs _seeded = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _logIndex = Substitute.For<ILogIndexStorage>();
        _logIndex.Enabled.Returns(true);
        _logIndex.MinBlockNumber.Returns((int?)null);
        _logIndex.MaxBlockNumber.Returns((int?)null);

        _node = await McpTestNode.Create(
            c =>
            {
                c.MaxConcurrentToolCalls = 64;
                c.MaxLogBlockRange = MaxLogBlockRange;
                c.MaxLogs = MaxLogs;
                c.MaxResultSize = MaxResultSize;
                c.MaxIndexedLogBlockRange = 1000;
            },
            b => b.AddSingleton(_logIndex));
        _seeded = await SeedLogs(_node, TransactionsPerBlock);
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
        if (_logIndex is not null) await _logIndex.DisposeAsync();
    }

    [TestCase(1, true)]
    [TestCase(4, true)]
    [TestCase(7, true)]
    [TestCase(null, true)]
    [TestCase(3, false)]
    [TestCase(null, false)]
    public async Task Paging_returns_every_log_exactly_once_in_order(int? limit, bool byTopic)
    {
        List<(string, object?)> args = [("fromBlock", Hex(_seeded.First)), ("toBlock", Hex(_seeded.Last))];
        if (byTopic) args.Add(("topics", Json(new object?[] { PagingTopic.ToString() })));

        (List<LogId> logs, List<JsonElement> pages) = await PageAll(_client, args, limit);

        Assert.That(logs, Is.EqualTo(_seeded.Expected), "paging must neither lose nor repeat logs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pages[^1].GetProperty("truncated").GetBoolean(), Is.False);
            Assert.That(pages[^1].TryGetProperty("nextCursor", out _), Is.False);
            McpAssert.Quantity(pages[0].GetProperty("fromBlock"), Hex(_seeded.First));
            McpAssert.Quantity(pages[^1].GetProperty("toBlock"), Hex(_seeded.Last));
            foreach (JsonElement page in pages)
            {
                Assert.That(page.GetProperty("logs").GetArrayLength(), Is.LessThanOrEqualTo(limit ?? MaxLogs));
                Assert.That(page.GetProperty("indexed").GetBoolean(), Is.False);
                Assert.That(Number(page, "toBlock") - Number(page, "fromBlock") + 1, Is.LessThanOrEqualTo((ulong)MaxLogBlockRange),
                    "a page must not scan more than MaxLogBlockRange blocks without the log index");
                Assert.That(Number(page, "fromBlock"), Is.LessThanOrEqualTo(Number(page, "toBlock")));
            }

            for (int i = 1; i < pages.Count; i++)
            {
                Assert.That(Number(pages[i], "fromBlock"), Is.GreaterThanOrEqualTo(Number(pages[i - 1], "toBlock")), "pages advance monotonically");
            }
        }
    }

    [Test]
    public async Task Pages_stay_within_the_byte_budget()
    {
        (List<LogId> logs, List<JsonElement> pages) = await PageAll(_client,
            [("fromBlock", Hex(_seeded.First)), ("toBlock", Hex(_seeded.Last)), ("topics", Json(new object?[] { PagingTopic.ToString() }))], limit: null);

        Assert.That(logs, Is.EqualTo(_seeded.Expected));
        foreach (JsonElement page in pages)
        {
            Assert.That(JsonSerializer.SerializeToUtf8Bytes(page).Length, Is.LessThan(MaxResultSize));
        }

        Assert.That(pages.Any(static p => p.GetProperty("truncated").GetBoolean() && p.GetProperty("logs").GetArrayLength() < MaxLogs), Is.True,
            "with this MaxResultSize some pages must be cut by the byte budget rather than by MaxLogs");
    }

    [Test]
    public async Task Range_over_the_block_limit_is_scanned_page_by_page()
    {
        (List<LogId> logs, List<JsonElement> pages) = await PageAll(_client, [("fromBlock", "earliest"), ("toBlock", Hex(_seeded.Last))], limit: MaxLogs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logs, Is.EqualTo(_seeded.Expected));
            Assert.That(pages, Has.Count.GreaterThanOrEqualTo((int)((_seeded.Last + 1 + MaxLogBlockRange - 1) / MaxLogBlockRange)));
            McpAssert.Quantity(pages[0].GetProperty("fromBlock"), "0x0");
            McpAssert.Quantity(pages[0].GetProperty("toBlock"), Hex(MaxLogBlockRange - 1));
        }
    }

    [Test]
    public async Task Log_index_coverage_widens_selective_pages()
    {
        LogId target = _seeded.Expected[^1];
        List<(string, object?)> byAddress = [("fromBlock", "earliest"), ("toBlock", Hex(_seeded.Last)), ("address", Json(new[] { target.Emitter.ToString() }))];

        _logIndex.MinBlockNumber.Returns(0);
        _logIndex.MaxBlockNumber.Returns((int)_seeded.Last);
        try
        {
            (List<LogId> indexedLogs, List<JsonElement> indexedPages) = await PageAll(_client, byAddress, limit: null);
            (List<LogId> anyLogs, List<JsonElement> anyPages) = await PageAll(_client, [("fromBlock", "earliest"), ("toBlock", Hex(_seeded.Last))], limit: MaxLogs);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(indexedPages, Has.Count.EqualTo(1), "the index covers the whole range, so one page scans it");
                Assert.That(indexedPages[0].GetProperty("indexed").GetBoolean(), Is.True);
                Assert.That(indexedLogs, Is.EqualTo(_seeded.Expected.Where(l => l.Emitter == target.Emitter).ToList()));
                Assert.That(anyPages[0].GetProperty("indexed").GetBoolean(), Is.False, "a filter without address or topic cannot use the index");
                Assert.That(anyLogs, Is.EqualTo(_seeded.Expected));
            }
        }
        finally
        {
            _logIndex.MinBlockNumber.Returns((int?)null);
            _logIndex.MaxBlockNumber.Returns((int?)null);
        }
    }

    [Test]
    public async Task Selective_pages_without_index_coverage_stay_bounded()
    {
        LogId target = _seeded.Expected[^1];

        (List<LogId> logs, List<JsonElement> pages) = await PageAll(_client,
            [("fromBlock", "earliest"), ("toBlock", Hex(_seeded.Last)), ("address", Json(new[] { target.Emitter.ToString() }))], limit: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logs, Is.EqualTo(_seeded.Expected.Where(l => l.Emitter == target.Emitter).ToList()));
            Assert.That(pages, Has.Count.GreaterThan(1));
            Assert.That(pages.All(static p => !p.GetProperty("indexed").GetBoolean()), Is.True);
        }
    }

    private static IEnumerable<TestCaseData> MismatchCases()
    {
        yield return new TestCaseData("topics").SetName("Cursor_with_other_topics_is_rejected");
        yield return new TestCaseData("fromBlock").SetName("Cursor_with_other_fromBlock_is_rejected");
        yield return new TestCaseData("toBlock").SetName("Cursor_with_other_toBlock_is_rejected");
        yield return new TestCaseData("address").SetName("Cursor_with_an_address_filter_added_is_rejected");
    }

    [TestCaseSource(nameof(MismatchCases))]
    public async Task Cursor_for_a_different_filter_is_rejected(string changed)
    {
        string cursor = await FirstCursor();

        List<(string, object?)> args =
        [
            ("fromBlock", changed == "fromBlock" ? Hex(_seeded.First + 1) : Hex(_seeded.First)),
            ("toBlock", changed == "toBlock" ? "latest" : Hex(_seeded.Last)),
            ("topics", Json(new object?[] { changed == "topics" ? OtherTopic.ToString() : PagingTopic.ToString() })),
            ("cursor", cursor),
            ("limit", 2)
        ];
        if (changed == "address") args.Add(("address", Json(new[] { _seeded.Expected[0].Emitter.ToString() })));

        JsonElement error = McpAssert.Error(await McpToolCalls.Call(_client, "get_logs", [.. args]), McpAssert.InvalidInput);

        Assert.That(error.GetProperty("message").GetString(), Does.Contain("cursor"));
    }

    [Test]
    public async Task Cursor_ignores_address_and_topic_alternative_order()
    {
        string a = _seeded.Expected[0].Emitter.ToString();
        string b = _seeded.Expected[^1].Emitter.ToString();
        object?[] topics = [new[] { PagingTopic.ToString(), OtherTopic.ToString() }];
        object?[] reordered = [new[] { OtherTopic.ToString(), PagingTopic.ToString() }];

        JsonElement first = McpAssert.Success(await McpToolCalls.Call(_client, "get_logs",
            [("fromBlock", Hex(_seeded.First)), ("toBlock", Hex(_seeded.Last)), ("address", Json(new[] { a, b })), ("topics", Json(topics)), ("limit", 1)]));
        JsonElement second = McpAssert.Success(await McpToolCalls.Call(_client, "get_logs",
            [("fromBlock", Hex(_seeded.First)), ("toBlock", Hex(_seeded.Last)), ("address", Json(new[] { b.ToUpperInvariant().Replace("0X", "0x"), a })),
                ("topics", Json(reordered)), ("limit", 1), ("cursor", first.GetProperty("nextCursor").GetString())]));

        Assert.That(LogIdOf(second.GetProperty("logs")[0]), Is.EqualTo(_seeded.Expected[1]));
    }

    [TestCase("flip")]
    [TestCase("truncate")]
    [TestCase("extend")]
    [TestCase("garbage")]
    [TestCase("empty")]
    public async Task Tampered_cursor_is_rejected(string tampering)
    {
        string cursor = await FirstCursor();
        int middle = cursor.Length / 2;
        string tampered = tampering switch
        {
            "flip" => cursor[..middle] + (cursor[middle] == 'A' ? 'B' : 'A') + cursor[(middle + 1)..],
            "truncate" => cursor[..^4],
            "extend" => cursor + "AAAA",
            "garbage" => "not a cursor!",
            _ => ""
        };

        JsonElement error = McpAssert.Error(await McpToolCalls.Call(_client, "get_logs",
            [("fromBlock", Hex(_seeded.First)), ("toBlock", Hex(_seeded.Last)), ("topics", Json(new object?[] { PagingTopic.ToString() })), ("cursor", tampered), ("limit", 2)]),
            McpAssert.InvalidInput);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("cursor"));
            McpAssert.NoInternals(error);
        }
    }

    [TestCase(0)]
    [TestCase(MaxLogs + 1)]
    public async Task Limit_outside_the_node_limit_is_rejected(int limit)
    {
        JsonElement error = McpAssert.Error(await McpToolCalls.Call(_client, "get_logs",
            [("fromBlock", Hex(_seeded.First)), ("toBlock", Hex(_seeded.Last)), ("limit", limit)]), McpAssert.InvalidInput);

        Assert.That(error.GetProperty("message").GetString(), Does.Contain(MaxLogs.ToString()));
    }

    [Test]
    public async Task Eth_module_log_limit_is_handled_by_splitting_the_page()
    {
        // Every three-block page of the seeded chain holds more than 20 logs, but no single block does.
        await using McpTestNode node = await McpTestNode.Create(
            c =>
            {
                c.MaxLogBlockRange = MaxLogBlockRange;
                c.MaxLogs = MaxLogs;
            },
            b => b.AddDecorator<IJsonRpcConfig>((_, rpc) =>
            {
                rpc.MaxLogsPerResponse = 20;
                return rpc;
            }));
        SeededLogs seeded = await SeedLogs(node, [3, 2, 1, 4, 1]);
        await using McpClient client = await node.CreateClient();

        (List<LogId> logs, _) = await PageAll(client, [("fromBlock", Hex(seeded.First)), ("toBlock", Hex(seeded.Last))], limit: null);

        Assert.That(logs, Is.EqualTo(seeded.Expected));
    }

    [Test]
    public async Task Stream_mode_truncation_resumes_after_the_last_log()
    {
        await using McpTestNode node = await McpTestNode.Create(
            c =>
            {
                c.MaxLogBlockRange = MaxLogBlockRange;
                c.MaxLogs = MaxLogs;
            },
            b => b.AddDecorator<IJsonRpcConfig>((_, rpc) =>
            {
                rpc.EnableLogsStreamMode = true;
                rpc.MaxLogsPerResponse = 7;
                return rpc;
            }));
        // Each block (5 logs) fits the stream cap, but every three-block page (15 logs) is cut short by it.
        SeededLogs seeded = await SeedLogs(node, [1, 1, 1, 1, 1]);
        await using McpClient client = await node.CreateClient();

        (List<LogId> logs, List<JsonElement> pages) = await PageAll(client, [("fromBlock", Hex(seeded.First)), ("toBlock", Hex(seeded.Last))], limit: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logs, Is.EqualTo(seeded.Expected));
            Assert.That(pages.Max(static p => p.GetProperty("logs").GetArrayLength()), Is.LessThanOrEqualTo(7));
        }
    }

    [Test]
    public async Task Cursor_pins_a_latest_range_end()
    {
        await using McpTestNode node = await McpTestNode.Create(c =>
        {
            c.MaxLogBlockRange = MaxLogBlockRange;
            c.MaxLogs = MaxLogs;
        });
        SeededLogs seeded = await SeedLogs(node, [2, 1, 2]);
        await using McpClient client = await node.CreateClient();
        (string, object?)[] args = [("fromBlock", Hex(seeded.First)), ("toBlock", "latest")];

        JsonElement first = McpAssert.Success(await McpToolCalls.Call(client, "get_logs", [.. args, ("limit", 3)]));
        await SeedLogs(node, [1]);
        (List<LogId> rest, _) = await PageAll(client, [.. args], limit: 3, first.GetProperty("nextCursor").GetString());

        List<LogId> all = [.. first.GetProperty("logs").EnumerateArray().Select(LogIdOf), .. rest];
        Assert.That(all, Is.EqualTo(seeded.Expected), "logs of blocks added after the first page must not appear");
    }

    private async Task<string> FirstCursor()
    {
        JsonElement page = McpAssert.Success(await McpToolCalls.Call(_client, "get_logs",
            [("fromBlock", Hex(_seeded.First)), ("toBlock", Hex(_seeded.Last)), ("topics", Json(new object?[] { PagingTopic.ToString() })), ("limit", 2)]));
        Assert.That(page.GetProperty("truncated").GetBoolean(), Is.True, "precondition: the first page must be truncated");
        return page.GetProperty("nextCursor").GetString()!;
    }

    private static async Task<(List<LogId> Logs, List<JsonElement> Pages)> PageAll(McpClient client, List<(string, object?)> args, int? limit, string? cursor = null)
    {
        List<LogId> logs = [];
        List<JsonElement> pages = [];
        do
        {
            List<(string, object?)> call = [.. args];
            if (limit is not null) call.Add(("limit", limit.Value));
            if (cursor is not null) call.Add(("cursor", cursor));

            JsonElement page = McpAssert.Success(await McpToolCalls.Call(client, "get_logs", [.. call]));
            pages.Add(page);
            logs.AddRange(page.GetProperty("logs").EnumerateArray().Select(LogIdOf));
            cursor = page.GetProperty("truncated").GetBoolean() ? page.GetProperty("nextCursor").GetString() : null;
            Assert.That(pages, Has.Count.LessThan(500), "paging must terminate");
        }
        while (cursor is not null);

        return (logs, pages);
    }

    /// <summary>Adds one block per entry of <paramref name="transactionsPerBlock"/>, each transaction deploying a contract whose init code emits <see cref="LogsPerTransaction"/> logs.</summary>
    private static async Task<SeededLogs> SeedLogs(McpTestNode node, int[] transactionsPerBlock)
    {
        PrivateKey sender = TestItem.PrivateKeyC;
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, sender.Address);
        List<LogId> expected = [];
        ulong first = 0;
        ulong last = 0;
        int counter = 0;

        for (int b = 0; b < transactionsPerBlock.Length; b++)
        {
            Transaction[] transactions = new Transaction[transactionsPerBlock[b]];
            Address[] emitters = new Address[transactions.Length];
            for (int t = 0; t < transactions.Length; t++)
            {
                Prepare code = Prepare.EvmCode;
                for (int l = 0; l < LogsPerTransaction; l++)
                {
                    code = code.StoreDataInMemory(0, ((UInt256)(++counter)).ToBigEndian()).Log(32, 0, [PagingTopic]);
                }

                emitters[t] = ContractAddress.From(sender.Address, nonce);
                transactions[t] = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce++).WithGasPrice(1)
                    .WithCode(code.Done).WithGasLimit(200_000).SignedAndResolved(node.Chain.EthereumEcdsa, sender).TestObject;
            }

            Block block = await node.Chain.AddBlock(transactions);
            Assert.That(block.Transactions, Has.Length.EqualTo(transactions.Length), "precondition: every seeded transaction must be mined");
            if (b == 0) first = block.Number;
            last = block.Number;

            long logIndex = 0;
            for (int t = 0; t < transactions.Length; t++)
            {
                for (int l = 0; l < LogsPerTransaction; l++)
                {
                    expected.Add(new LogId(block.Number, logIndex++, transactions[t].Hash!.ToString(), emitters[t]));
                }
            }
        }

        return new SeededLogs(first, last, expected);
    }

    private static LogId LogIdOf(JsonElement log) => new(
        ulong.Parse(log.GetProperty("blockNumber").GetString()![2..], System.Globalization.NumberStyles.HexNumber),
        long.Parse(log.GetProperty("logIndex").GetString()![2..], System.Globalization.NumberStyles.HexNumber),
        log.GetProperty("transactionHash").GetString()!,
        new Address(log.GetProperty("address").GetString()!));

    private static ulong Number(JsonElement page, string property) =>
        ulong.Parse(page.GetProperty(property).GetString()![2..], System.Globalization.NumberStyles.HexNumber);

    private static string Hex(ulong value) => McpAssert.Hex(value);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private sealed record LogId(ulong Block, long LogIndex, string TransactionHash, Address Emitter);

    private sealed record SeededLogs(ulong First, ulong Last, List<LogId> Expected);
}
