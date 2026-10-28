// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.TraceStore.Ots;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Test;

public class OtsRpcModuleTests
{
    [TestCase(true, 0ul, new[] { 3, 3 }, true, false)]
    [TestCase(true, 3ul, new[] { 1, 1 }, false, true)]
    [TestCase(true, 1ul, new int[0], false, true)]
    [TestCase(false, 0ul, new[] { 1, 1 }, false, true)]
    [TestCase(false, 1ul, new[] { 3, 3 }, true, false)]
    [TestCase(false, 3ul, new int[0], true, false)]
    [TestCase(false, ulong.MaxValue, new int[0], true, false)]
    [TestCase(true, 4ul, new[] { 3, 3 }, true, false)]
    [TestCase(true, ulong.MaxValue, new[] { 3, 3 }, true, false)]
    public void Search_preserves_exclusive_cursor_whole_blocks_order_and_flags(bool backwards, ulong cursor, int[] numbers, bool first, bool last)
    {
        using Fixture f = new();
        using ResultWrapper<OtsSearchResult> response = f.Search(backwards, cursor, 1);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        OtsSearchResult page = response.Data;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Txs.Select(t => t.BlockNumber), Is.EqualTo(numbers.Select(n => (ulong?)n)));
            Assert.That(page.Txs.Select(t => t.TransactionIndex), Is.EqualTo(numbers.Length == 0 ? [] : new long?[] { 1, 0 }));
            Assert.That(page.Receipts.Select(r => r.TransactionHash), Is.EqualTo(page.Txs.Select(t => t.Hash)));
            Assert.That(page.FirstPage, Is.EqualTo(first));
            Assert.That(page.LastPage, Is.EqualTo(last));
        }
    }

    [Test]
    public void Both_directions_return_the_same_complete_history([Values] bool backwards)
    {
        using Fixture f = new();
        using ResultWrapper<OtsSearchResult> response = f.Search(backwards, 0, 25);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Data.Txs.Select(t => t.BlockNumber), Is.EqualTo(new ulong?[] { 3, 3, 1, 1 }));
            Assert.That(response.Data.FirstPage && response.Data.LastPage, Is.True);
        }
    }

    [Test]
    public void Search_includes_zero_value_reverted_internal_calls_and_deduplicates()
    {
        using Fixture f = new();
        ParityLikeTxTrace trace = f.Traces[3][0];
        trace.Action!.To = TestItem.AddressC;
        trace.Action.Error = "Reverted";
        trace.Action.Subtraces = [Call(TestItem.AddressA), Call(TestItem.AddressA)];
        f.Save(3);
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 25);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        OtsSearchResult page = response.Data;
        Assert.That(page.Txs.Count(t => t.Hash == trace.TransactionHash), Is.EqualTo(1));
    }

    [Test]
    public void Log_only_involvement_and_rewards_are_not_transaction_call_involvement()
    {
        using Fixture f = new();
        f.Traces[3].Add(new() { Action = new() { Type = "reward", Author = TestItem.AddressF } });
        f.Save(3);
        using ResultWrapper<OtsSearchResult> response = f.Module.ots_searchTransactionsBefore(TestItem.AddressF, 0, 25);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        OtsSearchResult page = response.Data;
        Assert.That(page.Txs, Is.Empty);
    }

    [Test]
    public void Search_stops_after_one_more_matching_block()
    {
        using Fixture f = new();
        f.AddBlock(Call(TestItem.AddressA));
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 1);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(f.Serializer.Reads, Is.EqualTo(2));
            Assert.That(response.Data.LastPage, Is.False);
        }
    }

    [Test]
    public void Missing_history_is_an_error_even_after_a_full_page([Values] bool backwards)
    {
        using Fixture f = new();
        f.Store.Delete(f.Blocks[2].Hash!);
        using ResultWrapper<OtsSearchResult> response = f.Search(backwards, 0, 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(response.Result.Error, Does.Contain("block 2"));
            Assert.That(response.Data, Is.Null);
        }
    }

    [Test]
    public void Incomplete_or_mismatched_traces_are_not_silent_omissions(
        [Values("missing", "duplicate", "wrong-hash", "wrong-block", "no-action", "null-position", "negative-position", "position", "number")] string corruption)
    {
        using Fixture f = new();
        switch (corruption)
        {
            case "missing": f.Traces[3].RemoveAt(0); break;
            case "duplicate": f.Traces[3].Add(f.Traces[3][0]); break;
            case "wrong-hash": f.Traces[3][0].TransactionHash = TestItem.KeccakH; break;
            case "wrong-block": f.Traces[3][0].BlockHash = TestItem.KeccakH; break;
            case "no-action": f.Traces[3][0].Action = null; break;
            case "null-position": f.Traces[3][0].TransactionPosition = null; break;
            case "negative-position": f.Traces[3][0].TransactionPosition = -1; break;
            case "position": f.Traces[3][0].TransactionPosition = 2; break;
            case "number": f.Traces[3][0].BlockNumber = 1; break;
        }
        f.Save(3);
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 25);
        Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
    }

    [Test]
    public void Missing_receipts_are_an_error()
    {
        using Fixture f = new();
        f.Receipts.RemoveReceipts(f.Blocks[3]);
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 1);
        Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
    }

    [Test]
    public void Invalid_page_sizes_are_rejected_before_reading_traces([Values(-1, 0, 26, int.MaxValue)] int pageSize)
    {
        using Fixture f = new();
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, pageSize);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(f.Serializer.Reads, Is.Zero);
        }
    }

    [Test]
    public void Reorg_during_search_does_not_return_a_mixed_page()
    {
        using Fixture f = new();
        Block replacement = Build.A.Block.WithParent(f.Blocks[2]).WithExtraData([1]).TestObject;
        f.Serializer.BeforeRead = () =>
        {
            f.Serializer.BeforeRead = null;
            f.Tree.SuggestBlock(replacement);
            f.Tree.TryUpdateMainChain(replacement.Header, true, true, replacement);
        };
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 25);
        Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
    }

    [Test]
    public void Trace_types_must_include_calls()
    {
        using Fixture f = new();
        f.TraceConfig.TraceTypes = ParityTraceTypes.Rewards;
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 1);
        Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
    }

    [Test]
    public void Search_honors_timeout()
    {
        using Fixture f = new();
        f.RpcConfig.Timeout = 1;
        f.Serializer.BeforeRead = () => Thread.Sleep(50);
        Assert.Throws<OperationCanceledException>(() => f.Search(true, 0, 25));
    }

    [Test]
    public void Trace_maps_depth_types_nullable_values_and_revert_output()
    {
        using Fixture f = new();
        ParityTraceAction root = f.Traces[3][0].Action!;
        ParityTraceAction reverted = Call(TestItem.AddressD);
        reverted.Error = "Reverted";
        reverted.Result!.Output = [0xde, 0xad];
        reverted.Subtraces.Add(new() { Type = "suicide", From = TestItem.AddressD, To = TestItem.AddressA, Value = 7, Result = null });
        root.Subtraces =
        [
            new() { Type = "call", CallType = "staticcall", From = TestItem.AddressC, To = TestItem.AddressD },
            new() { Type = "call", CallType = "delegatecall", From = TestItem.AddressC, To = TestItem.AddressD, Value = 9 },
            new() { Type = "create", CreationMethod = "create2", From = TestItem.AddressC, To = TestItem.AddressD, Input = new byte[] { 1 }, Result = new() { Address = TestItem.AddressD, Code = [2] } },
            new() { Type = "call", CallType = "call", From = TestItem.AddressC, To = TestItem.AddressD, IsPrecompiled = true, Value = 1 },
            reverted
        ];
        f.Save(3);
        using ResultWrapper<List<OtsTraceEntry>> response = f.Module.ots_traceTransaction(f.Blocks[3].Transactions[0].Hash!);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        using JsonDocument json = Serialize(response);
        JsonElement entries = json.RootElement.GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Data.Select(e => e.Type), Is.EqualTo(new[] { "CALL", "STATICCALL", "DELEGATECALL", "CREATE2", "CALL", "SELFDESTRUCT" }));
            Assert.That(response.Data.Select(e => e.Depth), Is.EqualTo(new[] { 0, 1, 1, 1, 1, 2 }));
            Assert.That(entries[1].GetProperty("value").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(entries[2].GetProperty("value").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(entries[3].GetProperty("output").GetString(), Is.EqualTo("0x02"));
            Assert.That(entries[4].GetProperty("output").GetString(), Is.EqualTo("0xdead"));
            Assert.That(entries[5].GetProperty("input").GetString(), Is.EqualTo("0x"));
            Assert.That(entries[0].GetProperty("depth").ValueKind, Is.EqualTo(JsonValueKind.Number));
        }
    }

    [Test]
    public void Trace_maps_root_failures_without_an_entered_frame([Values] bool creation)
    {
        using Fixture f = new();
        f.AddBlock(new ParityTraceAction { From = TestItem.AddressB, To = creation ? null : TestItem.AddressC });
        Block block = f.Blocks[^1];
        Transaction tx = block.Transactions[0];
        Address recipient = creation ? ContractAddress.From(tx.SenderAddress, tx.Nonce) : tx.To!;
        ParityLikeTxTracer tracer = new(block, tx, ParityTraceTypes.Trace);
        tracer.MarkAsFailed(recipient, default, [], creation ? nameof(EvmExceptionType.TransactionCollision) : nameof(EvmExceptionType.OutOfGas));
        f.Traces[(int)block.Number][0] = tracer.BuildResult();
        f.Save((int)block.Number);

        using ResultWrapper<List<OtsTraceEntry>> response = f.Module.ots_traceTransaction(tx.Hash!);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        List<OtsTraceEntry> entries = response.Data;
        Assert.That(entries, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].Type, Is.EqualTo(creation ? "CREATE" : "CALL"));
            Assert.That(entries[0].Depth, Is.Zero);
            Assert.That(entries[0].From, Is.EqualTo(tx.SenderAddress));
            Assert.That(entries[0].To, Is.EqualTo(recipient));
            Assert.That(entries[0].Output, Is.Empty);
        }
    }

    [Test]
    public void Unknown_transaction_is_an_error()
    {
        using Fixture f = new();
        using ResultWrapper<List<OtsTraceEntry>> response = f.Module.ots_traceTransaction(TestItem.KeccakH);
        Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
    }

    [Test]
    public void Search_wire_format_includes_full_receipts_decimal_timestamp_and_block_log_indexes()
    {
        using Fixture f = new();
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 1);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        using JsonDocument json = Serialize(response);
        JsonElement receipt = json.RootElement.GetProperty("result").GetProperty("receipts")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GetProperty("timestamp").GetUInt64(), Is.EqualTo(f.Blocks[3].Timestamp));
            Assert.That(receipt.GetProperty("logs")[0].GetProperty("logIndex").GetString(), Is.EqualTo("0x1"));
            Assert.That(receipt.GetProperty("transactionHash").GetString(), Is.EqualTo(f.Blocks[3].Transactions[1].Hash!.ToString()));
        }
    }

    [Test]
    public void Production_module_registration_exposes_only_the_requested_subset()
    {
        using Fixture f = new();
        IRpcModuleProvider provider = f.Container.Resolve<IRpcModuleProvider>();
        using ResultWrapper<int> response = f.Module.ots_getApiLevel();
        using JsonDocument json = Serialize(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Enabled, Does.Contain(ModuleType.Ots));
            foreach (string method in new[] { "ots_getApiLevel", "ots_searchTransactionsBefore", "ots_searchTransactionsAfter", "ots_traceTransaction" })
                Assert.That(provider.Resolve(method)?.ReadOnly, Is.EqualTo(method == "ots_getApiLevel"), method);
            Assert.That(provider.Resolve("ots_getBlockDetails"), Is.Null);
            Assert.That(json.RootElement.GetProperty("result").GetInt32(), Is.Zero);
        }
    }

    [Test]
    public void Search_recovers_transaction_senders_from_cached_receipts()
    {
        using Fixture f = new();
        _ = f.Receipts.Get(f.Blocks[3]);
        foreach (Transaction transaction in f.Tree.FindBlock(3)!.Transactions) transaction.SenderAddress = null;
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 1);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        OtsSearchResult page = response.Data;
        Assert.That(page.Txs.Cast<LegacyTransactionForRpc>().Select(tx => tx.From), Is.All.EqualTo(TestItem.AddressB));
    }

    [Test]
    public void Empty_blocks_do_not_require_stored_transaction_traces()
    {
        using Fixture f = new();
        f.AddBlock();
        f.Store.Delete(f.Blocks[4].Hash!);
        using ResultWrapper<OtsSearchResult> response = f.Search(true, 0, 1);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        OtsSearchResult page = response.Data;
        Assert.That(page.Txs.Select(tx => tx.BlockNumber), Is.EqualTo(new ulong?[] { 3, 3 }));
    }

    [Test]
    public void Orphaned_transaction_traces_are_not_returned()
    {
        using Fixture f = new();
        Hash256 transactionHash = f.Blocks[3].Transactions[0].Hash!;
        Block replacement = Build.A.Block.WithParent(f.Blocks[2]).WithExtraData([1]).TestObject;
        f.Tree.SuggestBlock(replacement);
        f.Tree.TryUpdateMainChain(replacement.Header, true, true, replacement);
        using ResultWrapper<List<OtsTraceEntry>> response = f.Module.ots_traceTransaction(transactionHash);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public async Task Ots_requires_explicit_rpc_enablement()
    {
        using Fixture f = new();
        f.RpcConfig.EnabledModules = [];
        using JsonDocument args = JsonDocument.Parse("[]");
        JsonRpcContext context = new(RpcEndpoint.Http);
        using JsonRpcResponse response = await f.Container.Resolve<IJsonRpcService>().SendRequestAsync(
            new JsonRpcRequest { JsonRpc = "2.0", Id = 1, Method = "ots_getApiLevel", Params = args.RootElement }, context);
        using JsonDocument json = Serialize(response);
        Assert.That(json.RootElement.TryGetProperty("error", out _), Is.True);
    }

    [Test]
    public async Task Requests_bind_through_the_real_rpc_dispatcher(
        [Values("ots_getApiLevel", "ots_searchTransactionsBefore", "ots_searchTransactionsAfter", "ots_traceTransaction")] string method)
    {
        using Fixture f = new();
        string parameters = method switch
        {
            "ots_getApiLevel" => "[]",
            "ots_traceTransaction" => $"[\"{f.Blocks[3].Transactions[0].Hash}\"]",
            _ => $"[\"{TestItem.AddressA}\",0,25]"
        };
        using JsonDocument args = JsonDocument.Parse(parameters);
        JsonRpcContext context = new(RpcEndpoint.Http);
        using JsonRpcResponse response = await f.Container.Resolve<IJsonRpcService>().SendRequestAsync(
            new JsonRpcRequest { JsonRpc = "2.0", Id = 1, Method = method, Params = args.RootElement }, context);
        using JsonDocument json = Serialize(response);
        Assert.That(json.RootElement.TryGetProperty("result", out _), Is.True, json.RootElement.ToString());
    }

    public static IReleaseSpec[] ExecutionSpecs => [Shanghai.Instance, Osaka.Instance];

    [Test]
    public async Task Production_execution_persists_internal_call_history_including_failed_ancestors(
        [ValueSource(nameof(ExecutionSpecs))] IReleaseSpec spec, [Values] bool revert)
    {
        using BasicTestBlockchain chain = await CreateExecutionChain(spec);
        Prepare init = Prepare.EvmCode.CallWithValue(TestItem.AddressD, 50_000, 1);
        if (revert) init.PushData(0).PushData(0).Op(Instruction.REVERT);
        else init.Op(Instruction.STOP);
        Transaction tx = Build.A.Transaction.WithTo(null).WithNonce(0).WithGasLimit(200_000).WithGasPrice(2_000_000_000).WithValue(10)
            .WithData(init.Done).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(tx);
        Assert.That(block.Transactions, Has.Length.EqualTo(1));
        IOtsRpcModule module = chain.Container.Resolve<IRpcModuleFactory<IOtsRpcModule>>().Create();
        using ResultWrapper<OtsSearchResult> history = module.ots_searchTransactionsBefore(TestItem.AddressD, 0, 25);
        using ResultWrapper<List<OtsTraceEntry>> trace = module.ots_traceTransaction(tx.Hash!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Result.ResultType, Is.EqualTo(ResultType.Success), history.Result.Error);
            Assert.That(trace.Result.ResultType, Is.EqualTo(ResultType.Success), trace.Result.Error);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Data.Txs.Select(t => t.Hash), Is.EqualTo(new[] { tx.Hash }));
            Assert.That(history.Data.Receipts[0].Status, Is.EqualTo(revert ? 0 : 1));
            Assert.That(trace.Data.Select(t => t.Type), Is.EqualTo(new[] { "CREATE", "CALL" }));
            Assert.That(trace.Data[1].To, Is.EqualTo(TestItem.AddressD));
            Assert.That(trace.Data[1].Value, Is.EqualTo((Nethermind.Int256.UInt256)1));
        }
    }

    [Test]
    public void Walking_pages_has_no_duplicates_or_omissions([Values] bool backwards, [Values(1, 2, 3, 25)] int pageSize)
    {
        using Fixture f = new();
        f.AddBlock();
        f.AddBlock(Call(TestItem.AddressC));
        f.AddBlock(Call(TestItem.AddressA), Call(TestItem.AddressA), Call(TestItem.AddressA));
        f.AddBlock(Call(TestItem.AddressC));
        f.AddBlock(Call(TestItem.AddressA));
        Hash256[] expected = f.Blocks.SelectMany(b => b.Transactions)
            .Where(t => t.To == TestItem.AddressA).Select(t => t.Hash!).ToArray();
        List<Hash256> actual = [];
        ulong cursor = 0;
        for (int pageNumber = 0; ; pageNumber++)
        {
            Assert.That(pageNumber, Is.LessThan(f.Blocks.Count));
            using ResultWrapper<OtsSearchResult> response = f.Search(backwards, cursor, pageSize);
            Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
            OtsSearchResult page = response.Data;
            Assert.That(page.Txs.Select(t => (t.BlockNumber, t.TransactionIndex)), Is.Ordered.Descending);
            IEnumerable<Hash256> hashes = page.Txs.Select(t => t.Hash!);
            actual.AddRange(backwards ? hashes : hashes.Reverse());
            if (backwards ? page.LastPage : page.FirstPage) break;
            Assert.That(page.Txs, Is.Not.Empty);
            cursor = (backwards ? page.Txs[^1].BlockNumber : page.Txs[0].BlockNumber)!.Value;
        }
        Assert.That(actual, Is.EqualTo(backwards ? expected.Reverse() : expected));
    }

    [Test]
    public async Task Successful_outer_transaction_preserves_failed_child_involvement_but_not_its_balance_change(
        [ValueSource(nameof(ExecutionSpecs))] IReleaseSpec spec, [Values] bool childReverts)
    {
        using BasicTestBlockchain chain = await CreateExecutionChain(spec);
        Prepare childCode = Prepare.EvmCode.CallWithValue(TestItem.AddressF, 50_000, 1);
        if (childReverts) childCode.PushData(0).PushData(0).Op(Instruction.REVERT);
        else childCode.Op(Instruction.STOP);
        Transaction deployment = ExecutionTransaction(0, null, Prepare.EvmCode.ForInitOf(childCode.Done).Done, 0);
        Block deploymentBlock = await chain.AddBlock(deployment);
        Address child = chain.ReceiptStorage.Get(deploymentBlock)[0].ContractAddress!;
        Transaction outer = ExecutionTransaction(1, null, Prepare.EvmCode.CallWithValue(child, 150_000, 2).Op(Instruction.STOP).Done, 10);
        Block block = await chain.AddBlock(outer);
        IOtsRpcModule module = chain.Container.Resolve<IRpcModuleFactory<IOtsRpcModule>>().Create();
        using ResultWrapper<OtsSearchResult> history = module.ots_searchTransactionsBefore(TestItem.AddressF, 0, 25);
        using ResultWrapper<List<OtsTraceEntry>> trace = module.ots_traceTransaction(outer.Hash!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Result.ResultType, Is.EqualTo(ResultType.Success), history.Result.Error);
            Assert.That(trace.Result.ResultType, Is.EqualTo(ResultType.Success), trace.Result.Error);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Data.Txs.Select(t => t.Hash), Is.EqualTo(new[] { outer.Hash }));
            Assert.That(history.Data.Receipts[0].Status, Is.EqualTo(1));
            Assert.That(trace.Data.Select(t => t.Type), Is.EqualTo(new[] { "CREATE", "CALL", "CALL" }));
            Assert.That(trace.Data.Select(t => t.Depth), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(trace.Data[2].From, Is.EqualTo(child));
            Assert.That(trace.Data[2].To, Is.EqualTo(TestItem.AddressF));
            Assert.That(trace.Data[2].Value, Is.EqualTo((UInt256)1));
            Assert.That(chain.StateReader.GetBalance(block.Header, TestItem.AddressF), Is.EqualTo(childReverts ? UInt256.Zero : UInt256.One));
        }
    }

    [Test]
    public async Task Executed_creations_are_searchable_and_return_deployed_code(
        [ValueSource(nameof(ExecutionSpecs))] IReleaseSpec spec, [Values] bool create2)
    {
        using BasicTestBlockchain chain = await CreateExecutionChain(spec);
        byte[] runtime = [(byte)Instruction.STOP];
        byte[] init = Prepare.EvmCode.ForInitOf(runtime).Done;
        Prepare root = create2 ? Prepare.EvmCode.Create2(init, [1], 1) : Prepare.EvmCode.Create(init, 1);
        Transaction tx = ExecutionTransaction(0, null, root.Op(Instruction.STOP).Done, 10);
        Block block = await chain.AddBlock(tx);
        IOtsRpcModule module = chain.Container.Resolve<IRpcModuleFactory<IOtsRpcModule>>().Create();
        using ResultWrapper<List<OtsTraceEntry>> trace = module.ots_traceTransaction(tx.Hash!);
        Assert.That(trace.Result.ResultType, Is.EqualTo(ResultType.Success), trace.Result.Error);
        Address created = trace.Data[1].To;
        using ResultWrapper<OtsSearchResult> history = module.ots_searchTransactionsBefore(created, 0, 25);
        Assert.That(history.Result.ResultType, Is.EqualTo(ResultType.Success), history.Result.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Data.Txs.Select(t => t.Hash), Is.EqualTo(new[] { tx.Hash }));
            Assert.That(trace.Data.Select(t => t.Type), Is.EqualTo(new[] { "CREATE", create2 ? "CREATE2" : "CREATE" }));
            Assert.That(trace.Data[1].Depth, Is.EqualTo(1));
            Assert.That(trace.Data[1].Output, Is.EqualTo(runtime));
            Assert.That(chain.StateReader.GetCode(block.Header, created), Is.EqualTo(runtime));
            Assert.That(chain.StateReader.GetBalance(block.Header, created), Is.EqualTo(UInt256.One));
        }
    }

    [Test]
    public async Task Executed_selfdestruct_records_beneficiary_even_when_code_is_retained(
        [ValueSource(nameof(ExecutionSpecs))] IReleaseSpec spec, [Values] bool createdInSameTransaction)
    {
        using BasicTestBlockchain chain = await CreateExecutionChain(spec);
        byte[] code = Prepare.EvmCode.PushData(TestItem.AddressF).Op(Instruction.SELFDESTRUCT).Done;
        Transaction tx = ExecutionTransaction(0, null, createdInSameTransaction ? code : Prepare.EvmCode.ForInitOf(code).Done, 10);
        Block block = await chain.AddBlock(tx);
        Address contract = chain.ReceiptStorage.Get(block)[0].ContractAddress!;
        if (!createdInSameTransaction)
        {
            tx = ExecutionTransaction(1, contract, [], 0);
            block = await chain.AddBlock(tx);
        }
        IOtsRpcModule module = chain.Container.Resolve<IRpcModuleFactory<IOtsRpcModule>>().Create();
        using ResultWrapper<OtsSearchResult> history = module.ots_searchTransactionsBefore(TestItem.AddressF, 0, 25);
        using ResultWrapper<List<OtsTraceEntry>> trace = module.ots_traceTransaction(tx.Hash!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Result.ResultType, Is.EqualTo(ResultType.Success), history.Result.Error);
            Assert.That(trace.Result.ResultType, Is.EqualTo(ResultType.Success), trace.Result.Error);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Data.Txs.Select(t => t.Hash), Is.EqualTo(new[] { tx.Hash }));
            Assert.That(trace.Data.Select(t => t.Type), Is.EqualTo(new[] { createdInSameTransaction ? "CREATE" : "CALL", "SELFDESTRUCT" }));
            Assert.That(trace.Data[1].From, Is.EqualTo(contract));
            Assert.That(trace.Data[1].To, Is.EqualTo(TestItem.AddressF));
            Assert.That(trace.Data[1].Value, Is.EqualTo((UInt256)10));
            Assert.That(chain.StateReader.GetBalance(block.Header, TestItem.AddressF), Is.EqualTo((UInt256)10));
            Assert.That(chain.StateReader.GetCode(block.Header, contract), Is.EqualTo(!createdInSameTransaction && spec.IsEip6780Enabled ? code : []));
        }
    }

    private static Transaction ExecutionTransaction(ulong nonce, Address? to, byte[] data, UInt256 value) =>
        Build.A.Transaction.WithTo(to).WithNonce(nonce).WithGasLimit(500_000).WithGasPrice(2_000_000_000)
            .WithValue(value).WithData(data).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

    private static Task<BasicTestBlockchain> CreateExecutionChain(IReleaseSpec spec)
    {
        TraceStoreConfig traceConfig = new() { Enabled = true, BlocksToKeep = 0 };
        JsonRpcConfig rpcConfig = new();
        return BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(spec))
            .AddSingleton<ITraceStoreConfig>(traceConfig)
            .AddSingleton<IJsonRpcConfig>(rpcConfig)
            .AddModule(new TraceStorePlugin(traceConfig, rpcConfig).Module));
    }

    private static JsonDocument Serialize(JsonRpcResponse response)
    {
        ArrayBufferWriter<byte> buffer = new();
        JsonRpcResponseWriter.Write(buffer, response, EthereumJsonSerializer.JsonOptions);
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static ParityTraceAction Call(Address to) => new()
    {
        Type = "call",
        CallType = "call",
        From = TestItem.PrivateKeyB.Address,
        To = to
    };

    private sealed class Fixture : IDisposable, IAsyncDisposable
    {
        public IContainer Container { get; }
        public IBlockTree Tree { get; }
        public IReceiptStorage Receipts { get; }
        public IDb Store { get; }
        public IOtsRpcModule Module { get; }
        public RecordingSerializer Serializer { get; }
        public TraceStoreConfig TraceConfig { get; } = new() { Enabled = true, BlocksToKeep = 0 };
        public JsonRpcConfig RpcConfig { get; } = new() { EnabledModules = [ModuleType.Ots] };
        public List<Block> Blocks { get; } = [];
        public Dictionary<int, List<ParityLikeTxTrace>> Traces { get; } = [];

        public Fixture()
        {
            Container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(TraceConfig, RpcConfig, new ReceiptConfig { StoreReceipts = true, TxLookupLimit = 0 }))
                .AddModule(new TraceStorePlugin(TraceConfig, RpcConfig).Module)
                .AddDecorator<ITraceSerializer<ParityLikeTxTrace>>((_, inner) => new RecordingSerializer(inner))
                .AddSingleton<IJsonRpcService, JsonRpcService>()
                .Build();
            Tree = Container.Resolve<IBlockTree>();
            Receipts = Container.Resolve<IReceiptStorage>();
            Store = Container.ResolveKeyed<IDb>(TraceStorePlugin.DbName);
            Serializer = (RecordingSerializer)Container.Resolve<ITraceSerializer<ParityLikeTxTrace>>();
            Module = Container.Resolve<IRpcModuleFactory<IOtsRpcModule>>().Create();
            Block genesis = Build.A.Block.Genesis.TestObject;
            Tree.SuggestBlock(genesis);
            Tree.TryUpdateMainChain(genesis.Header, true, true, genesis);
            Blocks.Add(genesis);
            AddBlock(Call(TestItem.AddressA), Call(TestItem.AddressA));
            AddBlock(Call(TestItem.AddressC));
            AddBlock(Call(TestItem.AddressA), Call(TestItem.AddressA));
        }

        public ResultWrapper<OtsSearchResult> Search(bool backwards, ulong cursor, int pageSize) => backwards
            ? Module.ots_searchTransactionsBefore(TestItem.AddressA, cursor, pageSize)
            : Module.ots_searchTransactionsAfter(TestItem.AddressA, cursor, pageSize);

        public void AddBlock(params ParityTraceAction[] actions)
        {
            int number = Blocks.Count;
            Transaction[] transactions = actions.Select((a, i) => Build.A.Transaction
                .WithNonce(number * 10 + i).WithTo(a.To).SignedAndResolved(TestItem.PrivateKeyB).TestObject).ToArray();
            Block block = Build.A.Block.WithParent(Blocks[^1]).WithTransactions(transactions)
                .WithReceiptsRoot(TestItem.KeccakA).WithTimestamp((ulong)number * 12).TestObject;
            Tree.SuggestBlock(block);
            Tree.TryUpdateMainChain(block.Header, true, true, block);
            Blocks.Add(block);
            TxReceipt[] receipts = transactions.Select((tx, i) => new TxReceipt
            {
                TxHash = tx.Hash,
                Index = i,
                BlockHash = block.Hash,
                BlockNumber = block.Number,
                Sender = tx.SenderAddress,
                Recipient = tx.To,
                StatusCode = 1,
                GasUsed = 21000,
                GasUsedTotal = (ulong)(i + 1) * 21000,
                Logs = [new LogEntry(TestItem.AddressF, [], [TestItem.KeccakA])]
            }).ToArray();
            Receipts.Insert(block, receipts);
            Traces[number] = actions.Select((a, i) => new ParityLikeTxTrace
            {
                BlockHash = block.Hash,
                BlockNumber = block.Number,
                TransactionHash = transactions[i].Hash,
                TransactionPosition = i,
                Action = a
            }).ToList();
            Save(number);
        }

        public void Save(int number) => Store.Set(Blocks[number].Hash!, Serializer.Serialize(Traces[number]));
        public void Dispose() => Container.Dispose();
        public ValueTask DisposeAsync() => Container.DisposeAsync();
    }

    private sealed class RecordingSerializer(ITraceSerializer<ParityLikeTxTrace> inner) : ITraceSerializer<ParityLikeTxTrace>
    {
        public int Reads { get; private set; }
        public Action? BeforeRead { get; set; }
        public List<ParityLikeTxTrace>? Deserialize(Span<byte> serialized)
        {
            Reads++;
            BeforeRead?.Invoke();
            return inner.Deserialize(serialized);
        }
        public List<ParityLikeTxTrace>? Deserialize(Stream serialized) => inner.Deserialize(serialized);
        public byte[] Serialize(IReadOnlyCollection<ParityLikeTxTrace> traces) => inner.Serialize(traces);
    }
}
