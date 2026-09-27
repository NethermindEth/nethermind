// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Text;
using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.Self)]
public class McpActivityContentsTests
{
    [Test]
    public void Flow_metadata_distinguishes_unavailable_snapshots_from_changes()
    {
        McpTokenInfo first = new(TestItem.AddressA, "Token", "TOK", 18);
        McpTokenInfo changed = new(TestItem.AddressA, "Token", "NEW", 6);

        McpEthTools.ClassifyFlowMetadata([first, null], out McpTokenInfo? unavailableResult,
            out bool unavailableChanged, out bool unavailable);
        McpEthTools.ClassifyFlowMetadata([first, changed], out McpTokenInfo? changedResult,
            out bool didChange, out bool changedUnavailable);
        McpEthTools.ClassifyFlowMetadata([first, first], out McpTokenInfo? consistent,
            out bool sameChanged, out bool sameUnavailable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unavailableResult, Is.Null);
            Assert.That(unavailableChanged, Is.False);
            Assert.That(unavailable, Is.True);
            Assert.That(changedResult, Is.Null);
            Assert.That(didChange, Is.True);
            Assert.That(changedUnavailable, Is.False);
            Assert.That(consistent, Is.SameAs(first));
            Assert.That(sameChanged, Is.False);
            Assert.That(sameUnavailable, Is.False);
        }
    }
    private static readonly Address Mixed = new("0x1000000000000000000000000000000000000001");
    private static readonly Address Dense = new("0x1000000000000000000000000000000000000004");
    private static readonly Address Batch = new("0x1000000000000000000000000000000000000002");
    private static readonly Address Wrapped = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2");
    private static readonly Hash256 Transfer = TestContracts.TransferTopic;
    private static readonly Hash256 Single = Keccak.Compute("TransferSingle(address,address,address,uint256,uint256)");
    private static readonly Hash256 BatchTopic = Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])");
    private static readonly Hash256 Deposit = Keccak.Compute("Deposit(address,uint256)");
    private static readonly Hash256 Withdrawal = Keccak.Compute("Withdrawal(address,uint256)");

    private static async Task<(McpTestNode Node, Block Block)> Create(bool stream = false, int batchCalls = 0, ILogIndexStorage? index = null, bool instrumentPrices = false, int? cap = null)
    {
        McpTestNode node = await McpTestNode.Create(c => { if (index is not null) c.MaxLogBlockRange = 2; }, configureContainer: builder =>
        {
            if (index is not null) builder.AddSingleton(index);
            if (instrumentPrices) builder.AddDecorator<IRpcModuleProvider>(static (_, inner) => new McpPriceIsolationTests.PriceRpcProvider(inner));
            builder.AddScoped<IGenesisPostProcessor, ActivityGenesis>()
                .AddDecorator<IJsonRpcConfig>((_, rpc) => { rpc.EnableLogsStreamMode = stream; rpc.MaxLogsPerResponse = cap ?? (index is null ? 7 : 20_000); return rpc; });
        });
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, TestItem.AddressB);
        Address[] targets = batchCalls == 0 ? [Mixed, Wrapped] : Enumerable.Repeat(Batch, batchCalls).ToArray();
        Transaction[] transactions = targets.Select(target => Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId)
            .WithNonce(nonce++).WithGasPrice(1).WithTo(target).WithData(target == Wrapped ? McpAbiSignature.Parse("deposit()").Selector : []).WithGasLimit(1_000_000)
            .SignedAndResolved(node.Chain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject).ToArray();
        return (node, await node.Chain.AddBlock(transactions));
    }

    private static List<(string, object?)> Args(Block block, int limit = 50, string order = "asc") =>
        [("address", TestItem.AddressB.ToString()), ("fromBlock", block.Number.ToString()), ("toBlock", block.Number.ToString()),
            ("limit", limit), ("order", order)];

    [Test]
    public async Task Activity_matches_a_brute_force_receipt_scan(
        [Values(1, 2, 3)] int limit, [Values] bool stream, [Values("asc", "desc")] string order)
    {
        (McpTestNode created, Block block) = await Create(stream);
        await using McpTestNode node = created;
        await using McpClient client = await node.CreateClient();
        List<List<string>> expectedLogs = Reference(node.Chain.ReceiptStorage.Get(block));
        if (order == "desc") expectedLogs.Reverse();
        List<string> actual = [];
        int undecodable = 0;
        int pages = 0;
        string? cursor = null;
        do
        {
            List<(string, object?)> args = Args(block, limit, order);
            if (cursor is not null) args.Add(("cursor", cursor));
            CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
            JsonElement result = McpAssert.Success(call);
            actual.AddRange(result.GetProperty("movements").EnumerateArray().Select(Id));
            if (result.TryGetProperty("undecodableLogs", out JsonElement count)) undecodable += count.GetInt32();
            cursor = result.GetProperty("truncated").GetBoolean() ? result.GetProperty("nextCursor").GetString() : null;
            await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
            Assert.That(++pages, Is.LessThan(80), "the merged cursor must make progress");
        } while (cursor is not null);
        Assert.That(actual, Is.EqualTo(expectedLogs.SelectMany(static log => log)), "receipt reference: no duplicates, gaps, operator-only events or lost self transfers");
        Assert.That(undecodable, Is.EqualTo(1), "the malformed two-topic Transfer must be reported once across pages");
    }

    [Test]
    public async Task Activity_multiblock_reference_crosses_widening_windows_and_the_index([Values] bool stream, [Values("asc", "desc")] string order)
    {
        ILogIndexStorage index = Substitute.For<ILogIndexStorage>();
        index.Enabled.Returns(true);
        (McpTestNode created, Block first) = await Create(stream, index: index, instrumentPrices: true, cap: 7);
        await using McpTestNode node = created;
        List<Block> blocks = [first];
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(first.Header, TestItem.AddressB);
        foreach (int count in new[] { 0, 4, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })
        {
            Transaction[] transactions = Enumerable.Range(0, count).Select(_ => Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId)
                .WithNonce(nonce++).WithGasPrice(1).WithTo(Dense).WithGasLimit(1_000_000)
                .SignedAndResolved(node.Chain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject).ToArray();
            blocks.Add(await node.Chain.AddBlock(transactions));
        }
        index.MinBlockNumber.Returns((int)first.Number + 1);
        index.MaxBlockNumber.Returns((int)blocks[^1].Number);
        List<List<string>> expected = blocks.SelectMany(block => Reference(node.Chain.ReceiptStorage.Get(block))).ToList();
        if (order == "desc") expected.Reverse();
        await using McpClient client = await node.CreateClient();
        int multiBlockReads = 0;
        ((McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeCall = (method, args) =>
        {
            if (method == nameof(IEthRpcModule.eth_getLogs))
            {
                Filter filter = (Filter)args[0]!;
                if (filter.FromBlock.BlockNumber < filter.ToBlock.BlockNumber) multiBlockReads++;
            }
        };
        bool coveredPage = false;
        List<string> actual = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            List<(string, object?)> args = [.. Args(first, 50, order).Where(static arg => arg.Item1 != "toBlock"), ("toBlock", blocks[^1].Number.ToString())];
            if (cursor is not null) args.Add(("cursor", cursor));
            CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
            JsonElement result = McpAssert.Success(call);
            Assert.That(result.TryGetProperty("clampedFromBlock", out _), Is.False);
            coveredPage |= result.GetProperty("indexed").GetBoolean();
            actual.AddRange(result.GetProperty("movements").EnumerateArray().Select(Id));
            cursor = result.GetProperty("truncated").GetBoolean() ? result.GetProperty("nextCursor").GetString() : null;
            await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
            Assert.That(++pages, Is.LessThanOrEqualTo(5), "empty windows must not consume pages before the dense multi-block range");
        } while (cursor is not null);
        Assert.That(actual, Is.EqualTo(expected.SelectMany(static log => log)));
        Assert.That(multiBlockReads, Is.GreaterThan(1), "capped multi-block windows must narrow and recover");
        Assert.That(coveredPage, Is.True, "pages wholly inside the index must say indexed");
    }

    [Test]
    public async Task Activity_aggregates_only_fungible_returned_movements()
    {
        (McpTestNode created, Block block) = await Create();
        await using McpTestNode node = created;
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. Args(block)]);
        JsonElement result = McpAssert.Success(call);
        JsonElement flows = result.TryGetProperty("pageNetFlows", out JsonElement pageFlows) ? pageFlows : result.GetProperty("netFlows");
        BigInteger expected = 0;
        foreach (JsonElement movement in result.GetProperty("movements").EnumerateArray())
        {
            if (movement.GetProperty("standard").GetString() != "ERC-20") continue;
            BigInteger amount = BigInteger.Parse(movement.GetProperty("amount").GetString()!);
            expected += movement.GetProperty("direction").GetString() switch { "in" => amount, "out" => -amount, _ => 0 };
        }
        Assert.That(flows.EnumerateArray().Single(f => f.GetProperty("token").GetString()!.Equals(Mixed.ToString(), StringComparison.OrdinalIgnoreCase))
            .GetProperty("change").GetString(), Is.EqualTo(expected.ToString()), "NFT ids must not be added to fungible balances");
        Assert.That(result.TryGetProperty("pageNetFlows", out _), Is.True);
        Assert.That(result.GetProperty("pageTransactions").GetInt32(), Is.EqualTo(2));
        Assert.That(result.GetProperty("note").GetString(), Does.Contain("could not be decoded"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_wraps_use_the_contract_as_counterparty_and_avoid_minus_zero([Values] bool counterparty)
    {
        (McpTestNode created, Block block) = await Create();
        await using McpTestNode node = created;
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. Args(block), ("token", Wrapped.ToString()), ("includeUsd", true)]);
        JsonElement result = McpAssert.Success(call);
        if (counterparty)
            foreach (JsonElement movement in result.GetProperty("movements").EnumerateArray())
                Assert.That(movement.GetProperty("counterparty").GetString(), Is.EqualTo(Wrapped.ToString(true, true)));
        JsonElement flows = result.TryGetProperty("pageNetFlows", out JsonElement pageFlows) ? pageFlows : result.GetProperty("netFlows");
        Assert.That(flows[0].GetProperty("valueUsd").GetString(), Is.Not.EqualTo("-0"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_movement_bytes_stop_at_a_complete_log_and_resume()
    {
        (McpTestNode created, Block block) = await Create(batchCalls: 8);
        await using McpTestNode node = created;
        await using McpClient client = await node.CreateClient();
        string? cursor = null;
        int count = 0;
        int pages = 0;
        do
        {
            List<(string, object?)> args = [.. Args(block), ("maxBytes", 65_536)];
            if (cursor is not null) args.Add(("cursor", cursor));
            CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
            JsonElement result = McpAssert.Success(call);
            JsonElement movements = result.GetProperty("movements");
            Assert.That(Encoding.UTF8.GetByteCount(movements.GetRawText()), Is.LessThanOrEqualTo(65_536));
            Assert.That(movements.GetArrayLength() % 50, Is.Zero, "a TransferBatch must remain on one page");
            count += movements.GetArrayLength();
            cursor = result.GetProperty("truncated").GetBoolean() ? result.GetProperty("nextCursor").GetString() : null;
            if (cursor is not null) Assert.That(result.GetProperty("truncationReason").GetString(), Is.EqualTo("byte_budget"));
            await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
            Assert.That(++pages, Is.LessThan(10));
        } while (cursor is not null);
        Assert.That(count, Is.EqualTo(400));
    }

    [Test]
    public async Task Activity_undecodable_progress_precedes_an_oversized_event()
    {
        (McpTestNode created, Block block) = await Create();
        await using McpTestNode node = created;
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity",
            [.. Args(block, 50, "desc"), ("token", Mixed.ToString()), ("maxBytes", 1024)]);
        Assert.That(call.IsError, Is.Not.True, "an accepted log with no rows still advances the cursor");
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("undecodableLogs").GetInt32(), Is.EqualTo(1));
        Assert.That(result.GetProperty("movements").GetArrayLength(), Is.Zero);
        Assert.That(result.GetProperty("truncationReason").GetString(), Is.EqualTo("byte_budget"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_does_not_price_rows_removed_by_the_byte_budget()
    {
        (McpTestNode created, Block block) = await Create(instrumentPrices: true);
        await using McpTestNode node = created;
        int priceReads = 0;
        Address feed = new("0x5f4eC3Df9cbd43714FE2740f5E3616155c5b8419");
        ((McpPriceIsolationTests.PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeCall = (method, args) =>
        {
            if (method == nameof(IEthRpcModule.eth_call) && args[0] is LegacyTransactionForRpc { To: { } to } && to == feed)
            {
                priceReads++;
                throw new OperationCanceledException();
            }
        };
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. Args(block), ("maxBytes", 1024), ("includeUsd", true)]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("movements").EnumerateArray().All(row => row.GetProperty("token").GetString()!.Equals(Mixed.ToString(), StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(priceReads, Is.Zero, "the wrapped-token rows were trimmed before valuation");
        Assert.That(result.TryGetProperty("usdNotes", out _), Is.False, "discarded assets cannot add price notes");
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_optional_usd_preserves_rows_and_the_movement_byte_budget()
    {
        (McpTestNode created, Block block) = await Create();
        await using McpTestNode node = created;
        await using McpClient client = await node.CreateClient();
        List<(string, object?)> args = [.. Args(block), ("token", Wrapped.ToString())];
        JsonElement plain = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", [.. args]));
        int budget = Math.Max(1024, Encoding.UTF8.GetByteCount(plain.GetProperty("movements").GetRawText()) + 1);
        CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args, ("includeUsd", true), ("maxBytes", budget)]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("movements").GetArrayLength(), Is.EqualTo(plain.GetProperty("movements").GetArrayLength()),
            "optional fields must not evict accepted movement rows");
        Assert.That(Encoding.UTF8.GetByteCount(result.GetProperty("movements").GetRawText()), Is.LessThanOrEqualTo(budget));
        Assert.That(result.GetProperty("usdNotes").GetRawText(), Does.Contain("byte budget"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
    }

    [Test]
    public async Task Activity_cursor_accepts_equivalent_filters_and_rejects_changed_ones()
    {
        (McpTestNode created, Block block) = await Create();
        await using McpTestNode node = created;
        await using McpClient client = await node.CreateClient();
        List<(string, object?)> original = [.. Args(block, 1), ("token", Mixed.ToString())];
        JsonElement first = McpAssert.Success(await McpToolCalls.Call(client, "address_activity", [.. original]));
        string cursor = first.GetProperty("nextCursor").GetString()!;
        List<(string, object?)> equivalent =
        [
            ("address", "0x" + TestItem.AddressB.ToString()[2..].ToUpperInvariant()), ("token", Mixed.ToString(true, true)),
            ("fromBlock", $"0x{block.Number:x}"), ("toBlock", $"0x{block.Number:x}"), ("order", "asc"),
            ("limit", 1), ("includeUsd", true), ("cursor", cursor)
        ];
        CallToolResult resumed = await McpToolCalls.Call(client, "address_activity", [.. equivalent]);
        Assert.That(resumed.IsError, Is.Not.True, "equivalent numbers, address case and USD enrichment must preserve the cursor");
        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", resumed);
        foreach (string changed in new[] { "address", "token" })
        {
            List<(string, object?)> wrong = [.. original.Where(arg => arg.Item1 != changed), (changed, TestItem.AddressC.ToString()), ("cursor", cursor)];
            McpAssert.Error(await McpToolCalls.Call(client, "address_activity", [.. wrong]), "invalid_input");
        }
    }

    private static string Id(JsonElement movement) => string.Join('|',
        movement.GetProperty("transactionHash").GetString(), movement.GetProperty("token").GetString()!.ToLowerInvariant(),
        movement.GetProperty("standard").GetString(), movement.GetProperty("amount").GetString(),
        movement.TryGetProperty("tokenId", out JsonElement tokenId) ? tokenId.GetString() : "", movement.GetProperty("direction").GetString());

    private static List<List<string>> Reference(TxReceipt[] receipts)
    {
        List<List<string>> result = [];
        foreach (TxReceipt receipt in receipts)
        foreach (LogEntry log in receipt.Logs!)
        {
            Hash256[] topics = log.Topics;
            List<string> entries = [];
            if (topics.Length >= 3 && topics[0] == Transfer)
            {
                if (topics.Length == 3 && log.Data.Length == 32) Add("ERC-20", Account(1), Account(2), Word(log.Data, 0), null);
                else if (topics.Length == 4 && log.Data.Length == 0) Add("ERC-721", Account(1), Account(2), UInt256.One, new UInt256(topics[3].Bytes, true));
            }
            else if (topics.Length == 4 && topics[0] == Single)
                Add("ERC-1155", Account(2), Account(3), Word(log.Data, 1), Word(log.Data, 0));
            else if (topics.Length == 4 && topics[0] == BatchTopic)
            {
                int ids = (int)Word(log.Data, 0) / 32;
                int values = (int)Word(log.Data, 1) / 32;
                for (int i = 0; i < (int)Word(log.Data, ids); i++)
                    Add("ERC-1155", Account(2), Account(3), Word(log.Data, values + 1 + i), Word(log.Data, ids + 1 + i));
            }
            else if (log.Address == Wrapped && topics.Length == 2 && (topics[0] == Deposit || topics[0] == Withdrawal))
                Add("WETH", topics[0] == Deposit ? Address.Zero : Account(1), topics[0] == Deposit ? Account(1) : Address.Zero, Word(log.Data, 0), null);
            if (entries.Count > 0) result.Add(entries);

            Address Account(int index) => new(topics[index].Bytes[12..]);
            void Add(string standard, Address from, Address to, UInt256 amount, UInt256? id)
            {
                if (from != TestItem.AddressB && to != TestItem.AddressB) return;
                string direction = from == to ? "self" : to == TestItem.AddressB ? "in" : "out";
                entries.Add(string.Join('|', receipt.TxHash!.ToString(), log.Address.ToString(), standard, amount.ToString(), id?.ToString() ?? "", direction));
            }
        }
        return result;
    }

    private static UInt256 Word(byte[] data, int word) => new(data.AsSpan(word * 32, 32), true);
    private static Hash256 Topic(Address address) => new(address.Bytes.PadLeft(32));
    private static byte[] Emit(byte[] data, params Hash256[] topics) => Prepare.EvmCode.StoreDataInMemory(0, data)
        .Log(data.Length, 0, topics.Reverse().ToArray()).Done;

    private sealed class ActivityGenesis(IWorldState state, ISpecProvider specs) : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis)
        {
            genesis.Header.GasLimit = 30_000_000;
            List<byte[]> code = [];
            for (ulong i = 1; i <= 16; i++)
            {
                Address from = i % 2 == 0 ? TestItem.AddressC : TestItem.AddressB;
                Address to = i % 2 == 0 || i % 3 == 0 ? TestItem.AddressB : TestItem.AddressD;
                code.Add(Emit(((UInt256)i).ToBigEndian(), Transfer, Topic(from), Topic(to)));
            }
            code.Add(Emit([], Transfer, Topic(TestItem.AddressB), Topic(TestItem.AddressD), new Hash256(((UInt256)42).ToBigEndian())));
            code.Add(Emit(Bytes.Concat(((UInt256)7).ToBigEndian(), ((UInt256)3).ToBigEndian()), Single, Topic(TestItem.AddressC), Topic(TestItem.AddressB), Topic(TestItem.AddressD)));
            code.Add(Emit(McpTxTokensTests.BatchData(3), BatchTopic, Topic(TestItem.AddressC), Topic(TestItem.AddressD), Topic(TestItem.AddressB)));
            code.Add(Emit(McpTxTokensTests.BatchData(3), BatchTopic, Topic(TestItem.AddressB), Topic(TestItem.AddressC), Topic(TestItem.AddressD)));
            code.Add(Emit([], Transfer, Topic(TestItem.AddressB)));
            Install(Mixed, Bytes.Concat(code.ToArray()));
            Install(Dense, Bytes.Concat(Enumerable.Range(1, 20).Select(i => Emit(((UInt256)i).ToBigEndian(),
                Transfer, Topic(TestItem.AddressB), Topic(TestItem.AddressB))).ToArray()));
            Install(Batch, Emit(McpTxTokensTests.BatchData(50), BatchTopic, Topic(TestItem.AddressC), Topic(TestItem.AddressD), Topic(TestItem.AddressB)));
            Install(Wrapped, TestContracts.WrappedDeposit(TestItem.AddressB, 1, withdrawalAmount: 2));
            Install(new Address("0x5f4eC3Df9cbd43714FE2740f5E3616155c5b8419"),
                TestContracts.Aggregator(350_000_000_000, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10));
        }

        private void Install(Address address, byte[] code)
        {
            state.CreateAccount(address, UInt256.Zero);
            state.InsertCode(address, code, specs.GenesisSpec, isGenesis: true);
        }
    }
}
