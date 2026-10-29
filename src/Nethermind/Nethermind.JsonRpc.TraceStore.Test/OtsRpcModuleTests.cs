// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Precompiles;
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
using NSubstitute;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Db.LogIndex;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Repositories;

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
    public void Search_matches_sender_only_involvement([Values] bool backwards)
    {
        using Fixture f = new();
        using ResultWrapper<OtsSearchResult> response = backwards
            ? f.Module.ots_searchTransactionsBefore(TestItem.PrivateKeyB.Address, 0, 25)
            : f.Module.ots_searchTransactionsAfter(TestItem.PrivateKeyB.Address, 0, 25);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        Assert.That(response.Data.Txs.Select(t => t.Hash), Is.EqualTo(f.Blocks
            .SelectMany(b => b.Transactions).Reverse().Select(t => t.Hash)));
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
    public void Search_clears_pending_actions_after_an_early_match([Values] bool backwards)
    {
        using Fixture f = new();
        f.AddBlock(Call(TestItem.AddressC), Call(TestItem.AddressC));
        ParityTraceAction root = f.Traces[4][0].Action!;
        root.Subtraces = [Call(TestItem.AddressA), Call(TestItem.AddressA)];
        f.Save(4);
        using ResultWrapper<OtsSearchResult> response = f.Search(backwards, backwards ? 0ul : 3ul, 1);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        Assert.That(response.Data.Txs.Select(t => t.Hash), Is.EqualTo(new[] { f.Blocks[4].Transactions[0].Hash }));
    }

    [Test]
    public void Search_uses_each_scanned_blocks_precompile_spec([Values] bool backwards)
    {
        TestSpecProvider specs = new(Shanghai.Instance) { NextForkSpec = Cancun.Instance, ForkOnBlockNumber = 3 };
        using Fixture f = new(specProvider: specs);
        using ResultWrapper<OtsSearchResult> beforeFork = backwards
            ? f.Module.ots_searchTransactionsBefore(PrecompiledAddresses.PointEvaluation, 3, 25)
            : f.Module.ots_searchTransactionsAfter(PrecompiledAddresses.PointEvaluation, 0, 25);
        Assert.That(beforeFork.Result.ResultType, Is.EqualTo(backwards ? ResultType.Success : ResultType.Failure));
        if (backwards) Assert.That(beforeFork.Data.Txs, Is.Empty);
        else Assert.That(beforeFork.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));

        using ResultWrapper<OtsSearchResult> atFork = backwards
            ? f.Module.ots_searchTransactionsBefore(PrecompiledAddresses.PointEvaluation, 0, 25)
            : f.Module.ots_searchTransactionsAfter(PrecompiledAddresses.PointEvaluation, 2, 25);
        Assert.That(atFork.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
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
        Assert.Throws<InvalidDataException>(() => TracePositionRecord.Create(f.Blocks[3], f.Traces[3], f.Store.Get(f.Blocks[3].Hash!)!, CancellationToken.None));
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

    [TestCase(true, 1)]
    [TestCase(false, 3)]
    public void Search_rejects_switch_and_back_reorgs_at_scan_boundaries(bool backwards, int expectedReads)
    {
        using Fixture f = new();
        if (backwards)
        {
            f.Traces[2][0].Action = Call(TestItem.AddressA);
            f.Save(2);
        }
        Block sibling = StoreNonmatchingSibling(f, backwards ? f.Blocks[1] : f.Blocks[2]);
        Block tip = backwards ? Build.A.Block.WithParent(sibling).WithExtraData([1]).TestObject : sibling;
        if (backwards) f.Tree.SuggestBlock(tip);
        Block[] replacementBranch = backwards ? [sibling, tip] : [sibling];
        Block[] originalBranch = backwards ? [f.Blocks[2], f.Blocks[3]] : [f.Blocks[3]];
        int reads = 0;
        int switchRead = backwards ? 1 : 2;
        f.Serializer.BeforeRead = () =>
        {
            reads++;
            if (reads == switchRead)
                f.Tree.TryUpdateMainChain(tip.Header, true, true, replacementBranch);
            else if (reads == switchRead + 1)
                f.Tree.TryUpdateMainChain(f.Blocks[3].Header, true, true, originalBranch);
        };

        using ResultWrapper<OtsSearchResult> response = f.Search(backwards, 0, 25);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(response.Result.Error, Is.EqualTo("Canonical chain changed during OTS search; retry the request."));
            Assert.That(response.Data, Is.Null);
            Assert.That(f.Serializer.Reads, Is.EqualTo(expectedReads));
        }
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
        using CancellationTokenSource timeout = new();
        f.RpcConfig.Timeout = Timeout.Infinite;
        JsonRpcConfigExtension.UseForNextTimeoutInFlow(timeout);
        try
        {
            f.Serializer.BeforeRead = timeout.Cancel;
            Assert.Throws<OperationCanceledException>(() => f.Search(true, 0, 25));
        }
        finally
        {
            JsonRpcConfigExtension.EndTimeoutInFlow(timeout);
        }
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

        foreach (bool backwards in new[] { true, false })
        {
            using ResultWrapper<OtsSearchResult> history = backwards
                ? f.Module.ots_searchTransactionsBefore(recipient, 0, 25)
                : f.Module.ots_searchTransactionsAfter(recipient, 0, 25);
            Assert.That(history.Result.ResultType, Is.EqualTo(ResultType.Success), history.Result.Error);
            Assert.That(history.Data.Txs.Select(t => t.Hash), Does.Contain(tx.Hash));
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
    public void Search_log_indexes_include_logs_of_unmatched_preceding_transactions([Values] bool backwards)
    {
        using Fixture f = new();
        f.AddBlock(Call(TestItem.AddressC), Call(TestItem.AddressA));
        using ResultWrapper<OtsSearchResult> response = f.Search(backwards, backwards ? 0ul : 3ul, 1);
        Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
        Assert.That(response.Data.Txs, Has.Count.EqualTo(1));
        using JsonDocument json = Serialize(response);
        JsonElement receipt = json.RootElement.GetProperty("result").GetProperty("receipts")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Data.Txs[0].Hash, Is.EqualTo(f.Blocks[4].Transactions[1].Hash));
            Assert.That(receipt.GetProperty("logs")[0].GetProperty("logIndex").GetString(), Is.EqualTo("0x1"));
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
    public async Task Executed_rejected_creations_without_destinations_do_not_fail_the_trace(
        [ValueSource(nameof(ExecutionSpecs))] IReleaseSpec spec, [Values] bool create2)
    {
        using BasicTestBlockchain chain = await CreateExecutionChain(spec);
        byte[] init = [(byte)Instruction.STOP];
        Prepare root = create2 ? Prepare.EvmCode.Create2(init, [1], 1) : Prepare.EvmCode.Create(init, 1);
        root.Call(TestItem.AddressD, 50_000).Op(Instruction.STOP);
        Transaction tx = ExecutionTransaction(0, null, root.Done, 0);
        Block block = await chain.AddBlock(tx);
        Assert.That(block.Transactions, Has.Length.EqualTo(1));
        ParityTraceAction action = ReadExecutionTrace(chain, block).Action!;
        Assert.That(action.Subtraces, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(action.Subtraces[0].Type, Is.EqualTo("create"));
            Assert.That(action.Subtraces[0].CreationMethod, Is.EqualTo(create2 ? "create2" : "create"));
            Assert.That(action.Subtraces[0].Error, Is.EqualTo("Insufficient balance for transfer"));
            Assert.That(action.Subtraces[0].To, Is.Null);
            Assert.That(action.Subtraces[0].Result, Is.Null);
            Assert.That(chain.ReceiptStorage.Get(block)[0].StatusCode, Is.EqualTo(1));
        }
        IOtsRpcModule module = chain.Container.Resolve<IRpcModuleFactory<IOtsRpcModule>>().Create();
        using ResultWrapper<List<OtsTraceEntry>> trace = module.ots_traceTransaction(tx.Hash!);
        Assert.That(trace.Result.ResultType, Is.EqualTo(ResultType.Success), trace.Result.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Data.Select(e => e.Type), Is.EqualTo(new[] { "CREATE", "CALL" }));
            Assert.That(trace.Data.Select(e => e.Depth), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(trace.Data.Select(e => e.To), Is.EqualTo(new[] { ContractAddress.From(tx.SenderAddress, tx.Nonce), TestItem.AddressD }));
        }
    }

    private static IEnumerable<TestCaseData> PrecompileHistoryCases()
    {
        yield return new(Shanghai.Instance, PrecompiledAddresses.Identity, true);
        yield return new(Osaka.Instance, PrecompiledAddresses.Identity, true);
        yield return new(Shanghai.Instance, PrecompiledAddresses.PointEvaluation, false);
        yield return new(Cancun.Instance, PrecompiledAddresses.PointEvaluation, true);
    }

    [TestCaseSource(nameof(PrecompileHistoryCases))]
    public async Task Executed_zero_value_precompile_calls_cannot_certify_history(IReleaseSpec spec, Address destination, bool active)
    {
        using BasicTestBlockchain chain = await CreateExecutionChain(spec);
        Transaction tx = ExecutionTransaction(0, null, Prepare.EvmCode.Call(destination, 50_000).Op(Instruction.STOP).Done, 0);
        Block block = await chain.AddBlock(tx);
        Assert.That(block.Transactions, Has.Length.EqualTo(1));
        ParityTraceAction root = ReadExecutionTrace(chain, block).Action!;
        Assert.That(root.Subtraces, Has.Count.EqualTo(active ? 0 : 1));
        IOtsRpcModule module = chain.Container.Resolve<IRpcModuleFactory<IOtsRpcModule>>().Create();
        foreach (bool backwards in new[] { true, false })
        {
            using ResultWrapper<OtsSearchResult> history = backwards
                ? module.ots_searchTransactionsBefore(destination, 0, 25)
                : module.ots_searchTransactionsAfter(destination, 0, 25);
            if (active)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(history.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
                    Assert.That(history.Result.Error, Does.Contain("precompile history"));
                    Assert.That(history.Data, Is.Null);
                }
            }
            else
            {
                Assert.That(history.Result.ResultType, Is.EqualTo(ResultType.Success), history.Result.Error);
                Assert.That(history.Data.Txs.Select(t => t.Hash), Is.EqualTo(new[] { tx.Hash }));
            }
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

    private static ParityLikeTxTrace ReadExecutionTrace(BasicTestBlockchain chain, Block block)
    {
        IDb store = chain.Container.ResolveKeyed<IDb>(TraceStorePlugin.DbName);
        Span<byte> bytes = store.GetSpan(block.Hash!);
        try
        {
            Assert.That(bytes.IsEmpty, Is.False);
            List<ParityLikeTxTrace>? traces = chain.Container.Resolve<ITraceSerializer<ParityLikeTxTrace>>().Deserialize(bytes);
            Assert.That(traces, Is.Not.Null);
            ParityLikeTxTrace trace = traces!.Single(t => t.TransactionHash == block.Transactions[0].Hash);
            Assert.That(trace.Action, Is.Not.Null);
            return trace;
        }
        finally
        {
            store.DangerousReleaseMemory(bytes);
        }
    }

    private static Block StoreNonmatchingSibling(Fixture f, Block parent)
    {
        Transaction tx = Build.A.Transaction.WithNonce(parent.Number * 10 + 1).WithTo(TestItem.AddressC)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = Build.A.Block.WithParent(parent).WithTransactions([tx]).WithExtraData([1]).TestObject;
        f.Tree.SuggestBlock(block);
        ParityLikeTxTrace trace = new()
        {
            BlockHash = block.Hash,
            BlockNumber = block.Number,
            TransactionHash = tx.Hash,
            TransactionPosition = 0,
            Action = Call(TestItem.AddressC)
        };
        f.Store.Set(block.Hash!, f.Serializer.Serialize(new[] { trace }));
        return block;
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

        public bool PositionsEnabled { get; }

        public Fixture(IDbFactory? indexFactory = null, int fromBlock = 1, int lag = 0, bool resetIndex = false, bool positions = false,
            ILogManager? logManager = null, bool observeTraces = false, ISpecProvider? specProvider = null)
        {
            PositionsEnabled = positions;
            TraceConfig.AddressIndexEnabled = indexFactory is not null;
            TraceConfig.AddressIndexFromBlock = fromBlock;
            TraceConfig.AddressIndexLag = lag;
            TraceConfig.AddressIndexReset = resetIndex;
            ContainerBuilder builder = new ContainerBuilder()
                .AddModule(new TestNethermindModule(TraceConfig, RpcConfig, new ReceiptConfig { StoreReceipts = true, TxLookupLimit = 0 }))
                .AddModule(new TraceStorePlugin(TraceConfig, RpcConfig).Module)
                .AddDecorator<IDbFactory>((_, inner) => indexFactory is null ? inner : new IndexOnlyDbFactory(inner, indexFactory, observeTraces))
                .AddDecorator<ITraceSerializer<ParityLikeTxTrace>>((_, inner) => positions ? inner : new RecordingSerializer(inner))
                .AddSingleton<IJsonRpcService, JsonRpcService>();
            if (logManager is not null) builder.AddSingleton<ILogManager>(logManager);
            if (positions)
                builder.AddSingleton<TraceAddressIndex>(ctx => new(ctx.Resolve<IDbFactory>(),
                    ctx.ResolveKeyed<IDb>(TraceStorePlugin.DbName), ctx.Resolve<ITraceSerializer<ParityLikeTxTrace>>(),
                    ctx.Resolve<IBlockTree>(), TraceConfig, ctx.Resolve<ILogManager>(), true));
            if (specProvider is not null) builder.AddSingleton<ISpecProvider>(specProvider);
            Container = builder.Build();
            Tree = Container.Resolve<IBlockTree>();
            Receipts = Container.Resolve<IReceiptStorage>();
            Store = Container.ResolveKeyed<IDb>(TraceStorePlugin.DbName);
            ITraceSerializer<ParityLikeTxTrace> serializer = Container.Resolve<ITraceSerializer<ParityLikeTxTrace>>();
            Serializer = serializer as RecordingSerializer ?? new RecordingSerializer(serializer);
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

    [Test]
    public async Task Index_matches_scan_and_skips_unrelated_traces(
        [Values] bool backwards, [Values(1, 3, 25)] int pageSize, [Values] bool reverseViews, [Values] bool positions)
    {
        using IndexEnvironment env = new(reverseViews);
        await using Fixture indexed = new(env, positions: positions);
        await using Fixture scanned = new();
        foreach (Fixture f in new[] { indexed, scanned })
        {
            for (int i = 0; i < 10; i++) f.AddBlock(Call(TestItem.AddressC));
            f.AddBlock(Call(TestItem.AddressA));
            f.AddBlock();
        }
        await BuildIndex(indexed);
        int before = indexed.Serializer.Reads;
        ulong cursor = 0;
        do
        {
            using ResultWrapper<OtsSearchResult> expected = scanned.Search(backwards, cursor, pageSize);
            using ResultWrapper<OtsSearchResult> actual = indexed.Search(backwards, cursor, pageSize);
            Assert.That(actual.Result.ResultType, Is.EqualTo(ResultType.Success), actual.Result.Error);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.Data.Txs.Select(t => t.Hash), Is.EqualTo(expected.Data.Txs.Select(t => t.Hash)));
                Assert.That(actual.Data.FirstPage, Is.EqualTo(expected.Data.FirstPage));
                Assert.That(actual.Data.LastPage, Is.EqualTo(expected.Data.LastPage));
            }
            if (backwards ? actual.Data.LastPage : actual.Data.FirstPage) break;
            cursor = (backwards ? actual.Data.Txs[^1].BlockNumber : actual.Data.Txs[0].BlockNumber)!.Value;
        } while (true);
        Assert.That(indexed.Serializer.Reads - before, Is.LessThan(scanned.Serializer.Reads));
    }

    [Test]
    public async Task Index_includes_failed_root_creation_destinations([Values] bool backwards, [Values] bool positions)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: positions);
        f.AddBlock(new ParityTraceAction
        {
            CallType = "init",
            From = TestItem.PrivateKeyB.Address,
            Error = "Address collision",
            Result = null
        });
        Transaction tx = f.Blocks[4].Transactions[0];
        Address destination = ContractAddress.From(tx.SenderAddress, tx.Nonce);
        await BuildIndex(f);
        if (positions)
        {
            SearchResult<bool[]>? proof = f.Container.Resolve<TraceAddressIndex>()
                .TryMatchPositions(f.Blocks[4], destination, CancellationToken.None);
            Assert.That(proof.HasValue && !proof.Value.IsError, Is.True);
            Assert.That(proof!.Value.Object, Is.EqualTo(new[] { true }));
        }
        using ResultWrapper<OtsSearchResult> result = backwards
            ? f.Module.ots_searchTransactionsBefore(destination, 0, 25)
            : f.Module.ots_searchTransactionsAfter(destination, 0, 25);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
        OtsSearchResult page = result.Data;
        Assert.That(page.Txs.Select(t => t.Hash), Is.EqualTo(new[] { tx.Hash }));
    }

    [Test]
    public async Task Index_does_not_certify_precompile_history([Values] bool backwards, [Values] bool positions)
    {
        TestSpecProvider specs = new(Shanghai.Instance)
        {
            NextForkSpec = Cancun.Instance,
            ForkOnBlockNumber = 3,
            TransitionActivations = [(ForkActivation)0, (ForkActivation)3]
        };
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: positions, specProvider: specs);
        await BuildIndex(f);
        if (backwards)
        {
            using ResultWrapper<OtsSearchResult> beforeFork = f.Module.ots_searchTransactionsBefore(PrecompiledAddresses.PointEvaluation, 3, 25);
            Assert.That(beforeFork.Result.ResultType, Is.EqualTo(ResultType.Success), beforeFork.Result.Error);
            OtsSearchResult page = beforeFork.Data;
            Assert.That(page.Txs, Is.Empty);
        }
        using ResultWrapper<OtsSearchResult> result = backwards
            ? f.Module.ots_searchTransactionsBefore(PrecompiledAddresses.PointEvaluation, 0, 25)
            : f.Module.ots_searchTransactionsAfter(PrecompiledAddresses.PointEvaluation, 0, 25);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(result.Result.Error, Does.Contain("precompile history"));
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    public async Task Index_certifies_absent_address_without_deserializing([Values] bool backwards)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env);
        await BuildIndex(f);
        int before = f.Serializer.Reads;
        using ResultWrapper<OtsSearchResult> result = backwards
            ? f.Module.ots_searchTransactionsBefore(TestItem.AddressF, 0, 25)
            : f.Module.ots_searchTransactionsAfter(TestItem.AddressF, 0, 25);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Txs, Is.Empty);
            Assert.That(f.Serializer.Reads, Is.EqualTo(before));
        }
    }

    [Test]
    public async Task Index_includes_all_call_participants_and_excludes_rewards(
        [Values("from", "to", "create", "suicide", "precompile", "reverted", "excluded-flag", "reward")] string kind, [Values] bool positions)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: positions);
        ParityTraceAction child = Call(TestItem.AddressF);
        switch (kind)
        {
            case "from": child.From = TestItem.AddressF; child.To = TestItem.AddressE; break;
            case "create": child.Type = "create"; child.To = null; child.Result!.Address = TestItem.AddressF; break;
            case "suicide": child.Type = "suicide"; break;
            case "precompile": child.IsPrecompiled = true; break;
            case "reverted": child.Error = "Reverted"; break;
            case "excluded-flag": child.IncludeInTrace = false; break;
        }
        if (kind == "reward") f.Traces[3].Add(new() { Action = new() { Type = "reward", Author = TestItem.AddressF } });
        else f.Traces[3][0].Action!.Subtraces.Add(child);
        f.Save(3);
        await BuildIndex(f);
        using ResultWrapper<OtsSearchResult> result = f.Module.ots_searchTransactionsBefore(TestItem.AddressF, 0, 25);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
        Assert.That(result.Data.Txs.Count, Is.EqualTo(kind == "reward" ? 0 : 1));
    }

    [Test]
    public async Task Index_stops_at_missing_or_inconsistent_trace_and_resumes([Values("missing", "hash", "position", "duplicate", "action")] string corruption)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env);
        byte[] original = f.Store.Get(f.Blocks[2].Hash!)!;
        if (corruption == "missing") f.Store.Delete(f.Blocks[2].Hash!);
        else
        {
            switch (corruption)
            {
                case "hash": f.Traces[2][0].TransactionHash = TestItem.KeccakH; break;
                case "position": f.Traces[2][0].TransactionPosition = 8; break;
                case "duplicate": f.Traces[2].Add(f.Traces[2][0]); break;
                case "action": f.Traces[2][0].Action = null; break;
            }
            f.Save(2);
        }
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        await BuildIndex(f);
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Coverage.To, Is.EqualTo(1));
        using (ResultWrapper<OtsSearchResult> failed = f.Search(true, 0, 25))
            Assert.That(failed.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
        f.Store.Set(f.Blocks[2].Hash!, original);
        await BuildIndex(f);
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Coverage.To, Is.EqualTo(3));
    }

    [Test]
    public async Task Index_partial_coverage_scans_both_sides([Values] bool backwards)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, fromBlock: 2, lag: 1);
        await BuildIndex(f);
        int before = f.Serializer.Reads;
        using ResultWrapper<OtsSearchResult> result = f.Search(backwards, 0, 25);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Txs.Count, Is.EqualTo(4));
            Assert.That(f.Serializer.Reads - before, Is.EqualTo(2));
        }
        f.Store.Delete(f.Blocks[1].Hash!);
        using ResultWrapper<OtsSearchResult> missing = f.Search(backwards, 0, 25);
        Assert.That(missing.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
    }

    [Test]
    public async Task Index_selected_missing_data_remains_an_error([Values] bool receipts, [Values] bool positions)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: positions);
        await BuildIndex(f);
        if (receipts) f.Receipts.RemoveReceipts(f.Blocks[3]);
        else f.Store.Delete(f.Blocks[3].Hash!);
        using ResultWrapper<OtsSearchResult> result = f.Search(true, 0, 1);
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
    }

    [Test]
    public async Task Index_reorg_falls_back_or_rejects_active_query([Values] bool duringQuery, [Values] bool positions)
    {
        using IndexEnvironment env = new(observe: positions);
        await using Fixture f = new(env, positions: positions);
        await BuildIndex(f);
        void Reorg()
        {
            f.Serializer.BeforeRead = null;
            if (env.Controlled is not null) env.Controlled.Meta.BeforeSpan = null;
            Block replacement = Build.A.Block.WithParent(f.Blocks[2]).WithExtraData([1]).TestObject;
            f.Tree.SuggestBlock(replacement);
            f.Tree.TryUpdateMainChain(replacement.Header, true, true, replacement);
        }
        if (duringQuery)
        {
            if (positions) env.Controlled!.Meta.BeforeSpan = Reorg;
            else f.Serializer.BeforeRead = Reorg;
        }
        else Reorg();
        using ResultWrapper<OtsSearchResult> result = f.Search(true, 0, 25);
        if (duringQuery) Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
        else
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Txs.Select(t => t.BlockNumber), Is.EqualTo(new ulong?[] { 1, 1 }));
            Assert.That(f.Container.Resolve<TraceAddressIndex>().FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None), Is.Null);
        }
    }

    [Test]
    public async Task Index_rejects_progressively_published_reorg_levels(
        [Values] bool backwards, [Values("candidate", "lower-boundary", "upper-boundary")] string changed, [Values] bool positions)
    {
        using IndexEnvironment env = new();
        bool upper = changed == "upper-boundary";
        bool lower = changed == "lower-boundary";
        await using Fixture f = new(env, fromBlock: lower ? 2 : 1, lag: upper ? 2 : lower ? 1 : 0, positions: positions);
        if (upper) f.AddBlock();
        await BuildIndex(f);
        int height = upper ? 3 : 1;
        Block original = f.Blocks[height];
        Block parent = f.Blocks[height - 1];
        if (upper)
        {
            parent = Build.A.Block.WithParent(f.Blocks[1]).WithExtraData([8]).TestObject;
            f.Tree.SuggestBlock(parent);
        }
        Block replacement = Build.A.Block.WithParent(parent).WithTransactions(original.Transactions)
            .WithReceiptsRoot(TestItem.KeccakA).WithExtraData([9]).TestObject;
        f.Tree.SuggestBlock(replacement);
        List<ParityLikeTxTrace> traces = f.Serializer.Deserialize(f.Store.Get(original.Hash!)!)!;
        foreach (ParityLikeTxTrace trace in traces) trace.BlockHash = replacement.Hash;
        f.Store.Set(replacement.Hash!, f.Serializer.Serialize(traces));
        TxReceipt[] receipts = f.Receipts.Get(original);
        foreach (TxReceipt receipt in receipts) receipt.BlockHash = replacement.Hash;
        f.Receipts.Insert(replacement, receipts);

        IChainLevelInfoRepository levels = f.Container.Resolve<IChainLevelInfoRepository>();
        // PersistLevel publishes its cache entry before the batch is committed or Head changes.
        using BatchWrite batch = levels.StartBatch();
        levels.PersistLevel((ulong)height, new ChainLevelInfo(true, new BlockInfo(replacement.Hash!, 1)), batch);
        TraceAddressIndex.Candidates snapshot = f.Container.Resolve<TraceAddressIndex>()
            .FindCandidates(TestItem.AddressA, 1, (ulong)f.Blocks.Count - 1, backwards, 26, CancellationToken.None)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(f.Tree.Head!.Hash, Is.EqualTo(f.Blocks[^1].Hash));
            Assert.That(f.Tree.IsMainChain(f.Tree.Head.Header), Is.True);
            Assert.That(f.Tree.IsMainChain(replacement.Header), Is.True);
            Assert.That(f.Container.Resolve<TraceAddressIndex>().IsCanonical(snapshot.Coverage), Is.True);
        }
        using ResultWrapper<OtsSearchResult> result = f.Search(backwards, upper && backwards ? 4ul : 0, 1);
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
    }

    [Test]
    public async Task Index_restart_validates_checkpoint_and_namespace([Values("valid", "missing", "range", "hash", "config", "repair")] string state)
    {
        using IndexEnvironment env = new();
        await using (Fixture f = new(env))
        {
            await BuildIndex(f);
            byte[] checkpoint = env.Database!.GetColumnDb(LogIndexColumns.Meta).Get(TraceAddressIndex.CheckpointKey)!;
            if (state == "missing") env.Database.GetColumnDb(LogIndexColumns.Meta).Remove(TraceAddressIndex.CheckpointKey);
            if (state is "range" or "hash")
            {
                checkpoint[state == "range" ? 4 : ^1] ^= 1;
                env.Database.GetColumnDb(LogIndexColumns.Meta).Set(TraceAddressIndex.CheckpointKey, checkpoint);
            }
            Assert.That(env.Settings!.DbName, Is.EqualTo("TraceAddressIndex"));
            Assert.That(env.Settings.DbPath, Is.EqualTo("traceAddressIndex"));
            Assert.That(env.Settings.ColumnsMergeOperators, Has.Count.EqualTo(5));
        }
        if (state == "repair") File.WriteAllText(Path.Combine(env.Path, "traceAddressIndex", "repaired.marker"), "test");
        await using Fixture reopened = new(env, fromBlock: state == "config" ? 2 : 1);
        TraceAddressIndex.Candidates? candidates = reopened.Container.Resolve<TraceAddressIndex>()
            .FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None);
        if (state == "valid")
        {
            Assert.That(candidates!.Blocks, Is.EqualTo(new[] { 3, 1 }));
            Assert.That(reopened.Serializer.Reads, Is.Zero);
        }
        else Assert.That(candidates, Is.Null);
        using ResultWrapper<OtsSearchResult> result = reopened.Search(true, 0, 25);
        Assert.That(result.Data.Txs.Count, Is.EqualTo(4));
    }

    [Test]
    public async Task Index_reset_rebuilds_invalid_checkpoint()
    {
        using IndexEnvironment env = new();
        await using (Fixture original = new(env))
        {
            await BuildIndex(original);
            env.Database!.GetColumnDb(LogIndexColumns.Meta).Remove(TraceAddressIndex.CheckpointKey);
        }
        await using Fixture reset = new(env, resetIndex: true);
        TraceAddressIndex index = reset.Container.Resolve<TraceAddressIndex>();
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None), Is.Null);
        await BuildIndex(reset);
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Blocks, Is.EqualTo(new[] { 3, 1 }));
    }

    [Test]
    public async Task Index_cancellation_and_stop_are_safe()
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env);
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await index.BuildNextAsync(cancellation.Token));
        await BuildIndex(f);
        Assert.Throws<OperationCanceledException>(() => index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, cancellation.Token));
        index.Start();
        await Task.WhenAll(index.StopAsync(), index.StopAsync());
        await index.DisposeAsync();
        await index.DisposeAsync();
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None), Is.Null);
    }

    [Test]
    public void Index_rejects_unsupported_configuration([Values("pruning", "trace-type", "from", "lag")] string invalid)
    {
        using Fixture f = new();
        switch (invalid)
        {
            case "pruning": f.TraceConfig.BlocksToKeep = 1; break;
            case "trace-type": f.TraceConfig.TraceTypes = ParityTraceTypes.Rewards; break;
            case "from": f.TraceConfig.AddressIndexFromBlock = 0; break;
            case "lag": f.TraceConfig.AddressIndexLag = -1; break;
        }
        Assert.Throws<ArgumentException>(() => new TraceAddressIndex(new MemDbFactory(), f.Store, f.Serializer, f.Tree, f.TraceConfig, LimboLogs.Instance));
    }

    [Test]
    public async Task Index_backward_tail_is_bounded_without_losing_later_pages([Values] bool backwards, [Values] bool reverseViews)
    {
        using IndexEnvironment env = new(reverseViews);
        await using Fixture f = new(env);
        for (int i = 0; i < 40; i++) f.AddBlock(Call(TestItem.AddressA));
        await BuildIndex(f);
        TraceAddressIndex.Candidates candidates = f.Container.Resolve<TraceAddressIndex>()
            .FindCandidates(TestItem.AddressA, 1, 43, backwards, 26, CancellationToken.None)!;
        IEnumerable<int> expectedBlocks = Enumerable.Range(1, 43).Where(block => block != 2);
        Assert.That(candidates.Blocks, Is.EqualTo((backwards ? expectedBlocks.Reverse() : expectedBlocks).Take(26)));
        List<Hash256> actual = [];
        ulong cursor = 0;
        for (int page = 0; ; page++)
        {
            Assert.That(page, Is.LessThan(3));
            using ResultWrapper<OtsSearchResult> result = f.Search(backwards, cursor, 25);
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            actual.AddRange(result.Data.Txs.Select(t => t.Hash!));
            if (backwards ? result.Data.LastPage : result.Data.FirstPage) break;
            cursor = (backwards ? result.Data.Txs[^1].BlockNumber : result.Data.Txs[0].BlockNumber)!.Value;
        }
        Assert.That(actual.Distinct().Count(), Is.EqualTo(44));
        Assert.That(actual.Count, Is.EqualTo(44));
    }

    [Test]
    public async Task Index_and_event_index_have_independent_postings_and_metadata()
    {
        using IndexEnvironment env = new();
        await using ILogIndexStorage events = new LogIndexStorage(env, LimboLogs.Instance,
            new LogIndexConfig { Enabled = true, MaxReorgDepth = 0, CompressionDistance = 0, CompactionDistance = 0 });
        LogIndexAggregate logs = new(100, 100, false);
        logs.Address.Add(TestItem.AddressF, [100]);
        await events.AddReceiptsAsync(logs);
        await using Fixture f = new(env);
        await BuildIndex(f);
        using IEnumerator<int> found = events.GetEnumerator(TestItem.AddressF, 0, 200);
        Assert.That(found.MoveNext(), Is.True);
        Assert.That(found.Current, Is.EqualTo(100));
        Assert.That(events.MinBlockNumber, Is.EqualTo(100));
        TraceAddressIndex.Candidates candidates = f.Container.Resolve<TraceAddressIndex>()
            .FindCandidates(TestItem.AddressF, 1, 3, true, 26, CancellationToken.None)!;
        Assert.That(candidates.Blocks, Is.Empty);
        Assert.That(candidates.Coverage.To, Is.EqualTo(3));
    }

    [Test]
    public async Task Index_disposal_awaits_active_worker_before_closing_storage()
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        f.Serializer.BeforeRead = () =>
        {
            entered.Set();
            Assert.That(release.Wait(TimeSpan.FromSeconds(10)), Is.True);
        };
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        await f.Container.Resolve<StartTraceAddressIndex>().Execute(CancellationToken.None);
        Assert.That(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))), Is.True);
        Task disposing = index.DisposeAsync().AsTask();
        try { Assert.That(disposing.IsCompleted, Is.False); }
        finally { release.Set(); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Index_cancellation_during_action_walk_does_not_publish_coverage()
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env);
        using CancellationTokenSource cancellation = new();
        f.Serializer.BeforeRead = cancellation.Cancel;
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await index.BuildNextAsync(cancellation.Token));
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None), Is.Null);
        f.Serializer.BeforeRead = null;
        await BuildIndex(f);
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Coverage.To, Is.EqualTo(3));
    }

    [Test]
    public async Task Index_corrupt_compressed_trace_never_advances_coverage()
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env);
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        Assert.That(await index.BuildNextAsync(CancellationToken.None), Is.True);
        f.Store.Set(f.Blocks[2].Hash!, new byte[] { 1, 2, 3, 4 });
        Assert.That(async () => await index.BuildNextAsync(CancellationToken.None), Throws.Exception);
        Assert.That(index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Coverage.To, Is.EqualTo(1));
    }

    [Test]
    public async Task Position_sidecar_rejects_selected_mutations(
        [Values("missing", "corrupt", "changed", "transaction", "count", "order", "gzip-header", "unmatched-transaction")] string mutation)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: true);
        if (mutation == "unmatched-transaction")
        {
            f.Traces[3][1].Action!.To = TestItem.AddressC;
            f.Save(3);
        }
        await BuildIndex(f);
        Block block = f.Blocks[3];
        switch (mutation)
        {
            case "missing": f.Store.Delete(block.Hash!); break;
            case "corrupt": f.Store.Set(block.Hash!, new byte[] { 1, 2, 3 }); break;
            case "changed": f.Traces[3][0].Action!.Result!.GasUsed++; f.Save(3); break;
            case "transaction": block.Transactions[0].Hash = TestItem.KeccakH; break;
            case "unmatched-transaction": block.Transactions[1].Hash = TestItem.KeccakH; break;
            case "count": block = block.WithReplacedBody(block.Body.WithChangedTransactions([])); break;
            case "order": Array.Reverse(block.Transactions); break;
            case "gzip-header":
                byte[] blob = f.Store.Get(block.Hash!)!;
                blob[4] ^= 1;
                Assert.That(f.Serializer.Deserialize(blob)!.Count, Is.EqualTo(2));
                f.Store.Set(block.Hash!, blob);
                break;
        }
        SearchResult<bool[]>? result = f.Container.Resolve<TraceAddressIndex>()
            .TryMatchPositions(block, TestItem.AddressA, CancellationToken.None);
        Assert.That(result.HasValue && result.Value.IsError, Is.True);
    }

    [Test]
    public async Task Position_missing_or_old_record_uses_authoritative_path([Values] bool oldVersion)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: true);
        await BuildIndex(f);
        IDb meta = env.Database!.GetColumnDb(LogIndexColumns.Meta);
        byte[] key = TracePositionSidecar.Key(2, f.Blocks[3].Hash!);
        byte[]? record = meta.Get(key);
        if (oldVersion) BinaryPrimitives.WriteInt32LittleEndian(record!.AsSpan(4), 0);
        else record = null;
        meta.Set(key, record);
        using ResultWrapper<OtsSearchResult> result = f.Search(true, 0, 25);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data.Txs.Count, Is.EqualTo(4));
    }

    [Test]
    public async Task Position_preserves_existing_negative_routing_trust([Values] bool positions)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: positions);
        await BuildIndex(f);
        f.Traces[2][0].Action!.To = TestItem.AddressF;
        f.Save(2);
        using ResultWrapper<OtsSearchResult> result = f.Module.ots_searchTransactionsBefore(TestItem.AddressF, 0, 25);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data.Txs, Is.Empty, "Covered negative routing does not inspect a changed unselected blob.");
    }

    [Test, NonParallelizable]
    public async Task Position_registry_refresh_changes_options_identity_and_disables_proofs()
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: true);
        await BuildIndex(f);
        JsonSerializerOptions before = EthereumJsonSerializer.JsonOptions;
        EthereumJsonSerializer.AddTypeInfoResolver(new EmptyPositionResolver());
        Assert.That(EthereumJsonSerializer.JsonOptions, Is.Not.SameAs(before));
        Assert.That(f.Container.Resolve<TraceAddressIndex>().TryMatchPositions(f.Blocks[1], TestItem.AddressA, CancellationToken.None), Is.Null);
        using ResultWrapper<OtsSearchResult> result = f.Search(true, 0, 25);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
    }

    private sealed class EmptyPositionResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }

    [Test, NonParallelizable]
    public async Task Position_observability_reports_only_first_supported_match(
        [Values("success", "missing", "old", "invalid", "options")] string outcome)
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: true);
        await BuildIndex(f);
        TraceAddressIndex.Checkpoint coverage = f.Container.Resolve<TraceAddressIndex>()
            .FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Coverage;
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        TracePositionSidecar sidecar = new(env.Database!, f.Store, new ParityLikeTraceSerializer(LimboLogs.Instance),
            f.Tree, f.TraceConfig, new ILogger(logger));
        Assert.That(sidecar.BuildNext(coverage, CancellationToken.None), Is.False, "Load also runs when already caught up.");
        Assert.That(sidecar.BuildNext(coverage, CancellationToken.None), Is.False);
        logger.Received(1).Info(Arg.Is<string>(line => line.StartsWith("Position sidecar ready: next=4, eligible=True, schema=")));
        Block block = f.Blocks[1];
        IDb meta = env.Database!.GetColumnDb(LogIndexColumns.Meta);
        byte[] key = TracePositionSidecar.Key(2, block.Hash!);
        if (outcome == "missing") meta.Set(key, null);
        if (outcome == "invalid") meta.Set(key, new byte[] { 1 });
        if (outcome == "old")
        {
            byte[] record = meta.Get(key)!;
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), 0);
            meta.Set(key, record);
        }
        if (outcome == "options") EthereumJsonSerializer.AddTypeInfoResolver(new EmptyPositionResolver());
        for (int i = 0; i < 2; i++)
        {
            SearchResult<bool[]>? match = sidecar.TryMatch(block, TestItem.AddressA, CancellationToken.None);
            Assert.That(match.HasValue && !match.Value.IsError, Is.EqualTo(outcome == "success"));
        }
        logger.Received(outcome == "success" ? 1 : 0).Info(Arg.Is<string>(line =>
            line == $"Position sidecar first proof match: {block.Number} ({block.Hash})."));
    }

    [Test]
    public async Task Position_publication_preserves_v2_values_and_default_off([Values] bool positions)
    {
        using IndexEnvironment env = new();
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        await using Fixture f = new(env, positions: positions, logManager: new OneLoggerLogManager(new ILogger(logger)));
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        while (await index.BuildNextAsync(CancellationToken.None)) { }
        IDb meta = env.Database!.GetColumnDb(LogIndexColumns.Meta);
        KeyValuePair<byte[], byte[]>[] original = meta.GetAll().ToArray();
        while (index.BuildPositionsNext(CancellationToken.None)) { }
        using (Assert.EnterMultipleScope())
        {
            foreach (KeyValuePair<byte[], byte[]> item in original) Assert.That(meta.Get(item.Key), Is.EqualTo(item.Value));
            Assert.That(meta.Get(TracePositionSidecar.SchemaKey) is not null, Is.EqualTo(positions));
        }
        logger.Received(positions ? 1 : 0).Info(Arg.Is<string>(line => line.StartsWith("Position sidecar ready:")));
        logger.DidNotReceive().Info(Arg.Is<string>(line => line.StartsWith("Position sidecar first proof match:")));
    }

    [Test]
    public void Position_codec_rejects_malformed_records(
        [Values("empty", "truncated", "checksum", "count", "negative", "zero-group", "duplicate", "position", "trailing", "identity", "version-match", "negative-transactions", "negative-addresses", "transaction-overflow", "position-overflow", "record-limit", "record-over-limit")] string corruption)
    {
        using Fixture f = new();
        Block block = f.Blocks[1];
        byte[] blob = f.Store.Get(block.Hash!)!;
        byte[] record = TracePositionRecord.Create(block, f.Traces[1], blob, CancellationToken.None)!;
        switch (corruption)
        {
            case "empty": record = []; break;
            case "truncated": record = record[..^1]; break;
            case "checksum": record[^1] ^= 1; break;
            case "count": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(60), int.MaxValue); break;
            case "negative": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(64), -1); break;
            case "zero-group": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(152), 0); break;
            case "duplicate": record.AsSpan(156, 4).CopyTo(record.AsSpan(160, 4)); break;
            case "position": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(156), 2); break;
            case "trailing": record = [.. record, 0]; break;
            case "identity": record[20] ^= 1; break;
            case "version-match": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), TracePositionRecord.Version + 1); break;
            case "negative-transactions": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(52), -1); break;
            case "negative-addresses": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(60), -1); break;
            case "transaction-overflow": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(52), int.MaxValue); break;
            case "position-overflow": BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(64), int.MaxValue); break;
            case "record-limit": Array.Resize(ref record, TracePositionRecord.MaxRecordBytes); break;
            case "record-over-limit": Array.Resize(ref record, TracePositionRecord.MaxRecordBytes + 1); break;
        }
        if (corruption is "zero-group" or "duplicate" or "position")
            SHA256.HashData(record.AsSpan(0, record.Length - 32), record.AsSpan(record.Length - 32));
        if (corruption == "version-match")
            Assert.That(TracePositionRecord.Match(record, block, TestItem.AddressA, blob, CancellationToken.None), Is.Null);
        else
            Assert.Throws<InvalidDataException>(() => TracePositionRecord.Match(record, block, TestItem.AddressA, blob, CancellationToken.None));
    }

    [Test]
    public void Position_codec_preserves_all_positions_and_cancellation()
    {
        using Fixture f = new();
        Block block = f.Blocks[1];
        byte[] blob = f.Store.Get(block.Hash!)!;
        byte[] record = TracePositionRecord.Create(block, f.Traces[1], blob, CancellationToken.None)!;
        Assert.That(TracePositionRecord.Match(record, block, TestItem.AddressA, blob, CancellationToken.None), Is.EqualTo(new[] { true, true }));
        Assert.Throws<OperationCanceledException>(() => TracePositionRecord.Match(record, block, TestItem.AddressA, blob, new CancellationToken(true)));
    }

    [Test]
    public async Task Position_leases_are_released([Values("success", "cancel", "record", "source-io")] string outcome)
    {
        using IndexEnvironment env = new(observe: true);
        await using Fixture f = new(env, positions: true);
        await BuildIndex(f);
        ObservedDb source = new(f.Store);
        TracePositionSidecar sidecar = new(env.Database!, source, new ParityLikeTraceSerializer(LimboLogs.Instance),
            f.Tree, f.TraceConfig, LimboLogs.Instance.GetClassLogger<TracePositionSidecar>());
        TraceAddressIndex.Checkpoint coverage = f.Container.Resolve<TraceAddressIndex>()
            .FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Coverage;
        Assert.That(sidecar.BuildNext(coverage, CancellationToken.None), Is.False);
        using CancellationTokenSource cancellation = new();
        if (outcome == "cancel") source.BeforeSpan = cancellation.Cancel;
        if (outcome == "source-io") source.BeforeSpan = () => throw new IOException("Injected read failure");
        if (outcome == "record") env.Controlled!.Meta.Set(TracePositionSidecar.Key(2, f.Blocks[1].Hash!), new byte[] { 1 });
        if (outcome == "cancel")
            Assert.Throws<OperationCanceledException>(() => sidecar.TryMatch(f.Blocks[1], TestItem.AddressA, cancellation.Token));
        else if (outcome == "source-io")
            Assert.Throws<IOException>(() => sidecar.TryMatch(f.Blocks[1], TestItem.AddressA, cancellation.Token));
        else
            Assert.That(sidecar.TryMatch(f.Blocks[1], TestItem.AddressA, cancellation.Token)!.Value.IsError, Is.EqualTo(outcome == "record"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Leases, Is.Zero);
            Assert.That(env.Controlled!.Meta.Leases, Is.Zero);
        }
    }

    [Test]
    public async Task Position_disposal_waits_for_active_lookup()
    {
        using IndexEnvironment env = new(observe: true);
        await using Fixture f = new(env, positions: true);
        await BuildIndex(f);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        env.Controlled!.Meta.BeforeSpan = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        Task reading = Task.Run(() => index.TryMatchPositions(f.Blocks[1], TestItem.AddressA, CancellationToken.None));
        Assert.That(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))), Is.True);
        Task disposing = Task.Run(async () => await index.DisposeAsync());
        try
        {
            await Task.Delay(50);
            Assert.That(disposing.IsCompleted, Is.False);
        }
        finally { release.Set(); }
        await Task.WhenAll(reading, disposing).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(env.Controlled.Meta.Leases, Is.Zero);
    }

    [Test]
    public async Task Position_simulated_sync_failure_keeps_record_and_hint_together()
    {
        using IndexEnvironment env = new(observe: true);
        await using (Fixture f = new(env, positions: true))
        {
            TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
            while (await index.BuildNextAsync(CancellationToken.None)) { }
            Assert.That(index.BuildPositionsNext(CancellationToken.None), Is.True);
            env.Controlled!.BeforeSync = () => throw new IOException("Injected pre-fsync failure");
            Assert.Throws<IOException>(() => index.BuildPositionsNext(CancellationToken.None));
            env.Controlled.BeforeSync = null;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(env.Controlled.Meta.Get(TracePositionSidecar.Key(2, f.Blocks[2].Hash!)), Is.Not.Null);
                Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(env.Controlled.Meta.Get(TracePositionSidecar.ProgressKey)!), Is.EqualTo(3));
            }
        }
        // Clean close flushes the database: this tests injection/restart, not power-loss durability.
        await using Fixture restarted = new(env, positions: true);
        await BuildIndex(restarted);
    }

    [Test]
    public async Task Position_restart_handles_schema_and_resume_hint([Values("valid", "hint", "schema", "orphan")] string state)
    {
        using IndexEnvironment env = new();
        await using (Fixture f = new(env, positions: true))
        {
            await BuildIndex(f);
            IDb meta = env.Database!.GetColumnDb(LogIndexColumns.Meta);
            if (state == "hint") meta.Set(TracePositionSidecar.ProgressKey, new byte[] { 1 });
            if (state == "schema") meta.Set(TracePositionSidecar.SchemaKey, new byte[] { 1 });
            if (state == "orphan") meta.Set(TracePositionSidecar.SchemaKey, null);
            env.Database.SyncWal();
        }
        await using Fixture restarted = new(env, positions: true);
        TraceAddressIndex index = restarted.Container.Resolve<TraceAddressIndex>();
        while (index.BuildPositionsNext(CancellationToken.None)) { }
        Assert.That(index.TryMatchPositions(restarted.Blocks[1], TestItem.AddressA, CancellationToken.None) is not null,
            Is.EqualTo(state is "valid" or "hint"));
        using ResultWrapper<OtsSearchResult> result = restarted.Search(true, 0, 25);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data.Txs.Count, Is.EqualTo(4));
    }

    [Test]
    public async Task Position_oversized_inflation_skips_proof_without_limiting_rpc()
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: true);
        f.Traces[2][0].Action!.Input = new byte[TracePositionRecord.MaxBlobBytes];
        f.Save(2);
        await BuildIndex(f);
        Assert.That(f.Container.Resolve<TraceAddressIndex>().TryMatchPositions(f.Blocks[2], TestItem.AddressC, CancellationToken.None), Is.Null);
        using ResultWrapper<OtsSearchResult> result = f.Module.ots_searchTransactionsBefore(TestItem.AddressC, 0, 25);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data.Txs.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Position_builder_rejects_reentrancy_without_leaking_source_lease()
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: true);
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        while (await index.BuildNextAsync(CancellationToken.None)) { }
        TraceAddressIndex.Checkpoint coverage = index.FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Coverage;
        ObservedDb source = new(f.Store);
        TracePositionSidecar sidecar = new(env.Database!, source, new ParityLikeTraceSerializer(LimboLogs.Instance),
            f.Tree, f.TraceConfig, LimboLogs.Instance.GetClassLogger<TracePositionSidecar>());
        source.BeforeSpan = () => Assert.Throws<InvalidOperationException>(() => sidecar.BuildNext(coverage, CancellationToken.None));
        Assert.That(sidecar.BuildNext(coverage, CancellationToken.None), Is.True);
        Assert.That(source.Leases, Is.Zero);
    }

    [Test]
    public void Position_codec_skips_unsafe_or_excessive_shapes([Values("null-child", "null-list", "nodes")] string shape)
    {
        using Fixture f = new();
        ParityTraceAction root = f.Traces[1][0].Action!;
        if (shape == "null-child") root.Subtraces.Add(null!);
        if (shape == "null-list") root.Subtraces = null!;
        if (shape == "nodes") root.Subtraces = Enumerable.Repeat(new ParityTraceAction(), TracePositionRecord.MaxNodes).ToList();
        Assert.That(TracePositionRecord.Create(f.Blocks[1], f.Traces[1], f.Store.Get(f.Blocks[1].Hash!)!, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Position_partial_coverage_preserves_complete_response([Values] bool backwards)
    {
        using IndexEnvironment env = new();
        await using Fixture indexed = new(env, fromBlock: 2, positions: true);
        await using Fixture scanned = new();
        await BuildIndex(indexed);
        using ResultWrapper<OtsSearchResult> expected = scanned.Search(backwards, 0, 25);
        using ResultWrapper<OtsSearchResult> actual = indexed.Search(backwards, 0, 25);
        EthereumJsonSerializer json = new();
        Assert.That(json.Serialize(actual.Data), Is.EqualTo(json.Serialize(expected.Data)));
    }

    [Test]
    public async Task Position_custom_serializer_does_not_create_a_namespace()
    {
        using IndexEnvironment env = new();
        await using Fixture f = new(env);
        await BuildIndex(f);
        TraceAddressIndex.Checkpoint coverage = f.Container.Resolve<TraceAddressIndex>()
            .FindCandidates(TestItem.AddressA, 1, 3, true, 26, CancellationToken.None)!.Coverage;
        TracePositionSidecar sidecar = new(env.Database!, f.Store, f.Serializer, f.Tree, f.TraceConfig,
            LimboLogs.Instance.GetClassLogger<TracePositionSidecar>());
        Assert.That(sidecar.BuildNext(coverage, CancellationToken.None), Is.False);
        Assert.That(env.Database!.GetColumnDb(LogIndexColumns.Meta).Get(TracePositionSidecar.SchemaKey), Is.Null);
    }

    [Test]
    public async Task Position_adapter_read_failure_uses_existing_rpc_exception_contract()
    {
        using IndexEnvironment env = new(observe: true);
        await using Fixture f = new(env, positions: true);
        await BuildIndex(f);
        int proofReads = 0;
        env.Controlled!.Meta.OnSpanKey = key =>
        {
            if (key.AsSpan().StartsWith(TracePositionSidecar.Prefix) && key[TracePositionSidecar.Prefix.Length] == 2)
            {
                proofReads++;
                throw new IOException("Injected adapter read failure");
            }
        };
        using JsonDocument args = JsonDocument.Parse($"[\"{TestItem.AddressA}\",0,25]");
        JsonRpcContext context = new(RpcEndpoint.Http);
        using JsonRpcResponse response = await f.Container.Resolve<IJsonRpcService>().SendRequestAsync(
            new JsonRpcRequest { JsonRpc = "2.0", Id = 1, Method = "ots_searchTransactionsBefore", Params = args.RootElement }, context);
        using JsonDocument json = Serialize(response);
        Assert.That(json.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InternalError));
        Assert.That(proofReads, Is.EqualTo(1));
        Assert.That(env.Controlled.Meta.Leases, Is.Zero);
        env.Controlled.Meta.OnSpanKey = null;
        using ResultWrapper<OtsSearchResult> recovered = f.Search(true, 0, 25);
        Assert.That(recovered.Result.ResultType, Is.EqualTo(ResultType.Success));
    }

    [Test]
    public void Position_record_count_boundaries([Values("transactions", "addresses", "positions")] string limit, [Values(0, 1)] int over)
    {
        using Fixture f = new();
        int count = limit == "addresses" ? 1 : TracePositionRecord.MaxTransactions + (limit == "transactions" ? over : 0);
        Block block = f.Blocks[1].WithReplacedBody(f.Blocks[1].Body.WithChangedTransactions(
            Enumerable.Repeat(f.Blocks[1].Transactions[0], count).ToArray()));
        List<ParityLikeTxTrace> traces = Enumerable.Range(0, count).Select(position => new ParityLikeTxTrace
        {
            BlockHash = block.Hash,
            BlockNumber = block.Number,
            TransactionPosition = position,
            TransactionHash = block.Transactions[position].Hash,
            Action = Call(TestItem.AddressA)
        }).ToList();
        if (limit == "addresses")
        {
            traces[0].Action = new() { Result = null };
            for (int i = 0; i < TracePositionRecord.MaxAddresses + over; i++)
            {
                byte[] bytes = new byte[20];
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), i);
                traces[0].Action!.Subtraces.Add(new() { To = new Address(bytes) });
            }
        }
        if (limit == "positions")
        {
            foreach (ParityLikeTxTrace trace in traces)
            {
                trace.Action!.Result!.Address = TestItem.AddressC;
                trace.Action.Subtraces.Add(Call(TestItem.AddressD));
            }
            if (over != 0) traces[0].Action!.From = TestItem.AddressF;
        }
        byte[] blob = f.Store.Get(f.Blocks[1].Hash!)!;
        byte[]? record = TracePositionRecord.Create(block, traces, blob, CancellationToken.None);
        if (over != 0) Assert.That(record, Is.Null);
        else Assert.That(TracePositionRecord.Match(record!, block, TestItem.AddressA, blob, CancellationToken.None)!.Length, Is.EqualTo(count));
    }

    [Test]
    public async Task Position_failed_hint_staging_does_not_commit_partial_batch([Values] bool schema)
    {
        using IndexEnvironment env = new(observe: true);
        await using Fixture f = new(env, positions: true);
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        while (await index.BuildNextAsync(CancellationToken.None)) { }
        if (!schema) Assert.That(index.BuildPositionsNext(CancellationToken.None), Is.True);
        byte[] key = schema ? TracePositionSidecar.SchemaKey : TracePositionSidecar.Key(2, f.Blocks[2].Hash!);
        byte[]? hint = env.Controlled!.Meta.Get(TracePositionSidecar.ProgressKey);
        env.Controlled.FailHintWrite = true;
        Assert.Throws<IOException>(() => index.BuildPositionsNext(CancellationToken.None));
        Assert.That(env.Controlled.Meta.Get(key), Is.Null);
        Assert.That(env.Controlled.Meta.Get(TracePositionSidecar.ProgressKey), Is.EqualTo(hint));
        env.Controlled.FailHintWrite = false;
        Assert.That(index.BuildPositionsNext(CancellationToken.None), Is.True);
    }

    private static async Task BuildIndex(Fixture fixture)
    {
        TraceAddressIndex index = fixture.Container.Resolve<TraceAddressIndex>();
        int count = 0;
        while (await index.BuildNextAsync(CancellationToken.None))
            Assert.That(++count, Is.LessThan(fixture.Blocks.Count));
        if (fixture.PositionsEnabled)
        {
            count = 0;
            while (index.BuildPositionsNext(CancellationToken.None))
                Assert.That(++count, Is.LessThan(fixture.Blocks.Count));
            Assert.That(index.TryMatchPositions(fixture.Blocks[fixture.TraceConfig.AddressIndexFromBlock], TestItem.AddressA, CancellationToken.None), Is.Not.Null);
        }
    }

    [Test, NonParallelizable]
    public async Task Position_bulk_worker_catches_up_and_preserves_results([Values] bool bulk, [Values] bool backwards)
    {
        using BulkWorkerSwitch _ = new(bulk);
        using IndexEnvironment env = new();
        TaskCompletionSource caughtUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        logger.When(x => x.Info(Arg.Is<string>(line => line.StartsWith("Position sidecar caught up through"))))
            .Do(call => caughtUp.TrySetResult());
        await using Fixture indexed = new(env, positions: true, logManager: new OneLoggerLogManager(new ILogger(logger)));
        await using Fixture scanned = new();
        for (int i = 0; i < 67; i++)
        {
            indexed.AddBlock(Call(TestItem.AddressA));
            scanned.AddBlock(Call(TestItem.AddressA));
        }
        TraceAddressIndex index = indexed.Container.Resolve<TraceAddressIndex>();
        while (await index.BuildNextAsync(CancellationToken.None)) { }
        IDb meta = env.Database!.GetColumnDb(LogIndexColumns.Meta);
        KeyValuePair<byte[], byte[]>[] original = meta.GetAll().ToArray();
        index.Start();
        await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(index.TryMatchPositions(indexed.Blocks[1], TestItem.AddressA, CancellationToken.None), Is.Not.Null);
        using ResultWrapper<OtsSearchResult> expected = scanned.Search(backwards, 0, 25);
        using ResultWrapper<OtsSearchResult> actual = indexed.Search(backwards, 0, 25);
        EthereumJsonSerializer json = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json.Serialize(actual.Data), Is.EqualTo(json.Serialize(expected.Data)));
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(meta.Get(TracePositionSidecar.ProgressKey)!), Is.EqualTo((ulong)indexed.Blocks.Count));
            foreach (KeyValuePair<byte[], byte[]> item in original) Assert.That(meta.Get(item.Key), Is.EqualTo(item.Value));
        }
        await index.StopAsync();
    }

    [Test, NonParallelizable]
    public async Task Position_bulk_disposal_waits_for_work_and_releases_source([Values] bool bulk)
    {
        using BulkWorkerSwitch _ = new(bulk);
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: true, observeTraces: true);
        ObservedDb source = (ObservedDb)f.Store;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        source.BeforeSpan = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        index.Start();
        Assert.That(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))), Is.True);
        Task disposing = index.DisposeAsync().AsTask();
        try { Assert.That(disposing.IsCompleted, Is.False); }
        finally { release.Set(); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        await index.StopAsync();
        await index.DisposeAsync();
        Assert.That(source.Leases, Is.Zero);
    }

    [Test, NonParallelizable]
    public async Task Position_bulk_idle_worker_does_not_spin_on_missing_source([Values] bool bulk)
    {
        using BulkWorkerSwitch _ = new(bulk);
        using IndexEnvironment env = new();
        await using Fixture f = new(env, positions: true, observeTraces: true);
        TraceAddressIndex index = f.Container.Resolve<TraceAddressIndex>();
        while (await index.BuildNextAsync(CancellationToken.None)) { }
        f.Store.Delete(f.Blocks[1].Hash!);
        ObservedDb source = (ObservedDb)f.Store;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        source.BeforeSpan = () =>
        {
            Interlocked.Increment(ref reads);
            entered.TrySetResult();
        };
        index.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        await index.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(Volatile.Read(ref reads), Is.EqualTo(1));
        Assert.That(source.Leases, Is.Zero);
    }

    private sealed class BulkWorkerSwitch : IDisposable
    {
        private const string Key = "Nethermind.Experimental.OtsPositionSidecarBulkBuild";
        private readonly bool _previous;
        public BulkWorkerSwitch(bool enabled)
        {
            _previous = AppContext.TryGetSwitch(Key, out bool value) && value;
            AppContext.SetSwitch(Key, enabled);
        }
        public void Dispose() => AppContext.SetSwitch(Key, _previous);
    }

    private sealed class IndexOnlyDbFactory(IDbFactory inner, IDbFactory index, bool observeTraces = false) : IDbFactory
    {
        public IDb CreateDb(DbSettings settings)
        {
            IDb db = inner.CreateDb(settings);
            return observeTraces && settings.DbName == TraceStorePlugin.DbName ? new ObservedDb(db, disposeInner: true) : db;
        }
        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings settings) where T : struct, Enum =>
            (settings.DbName == TraceAddressIndex.DbName ? index : inner).CreateColumnsDb<T>(settings);
    }

    private sealed class ForwardOnlyColumns<T>(IColumnsDb<T> inner) : IColumnsDb<T> where T : struct, Enum
    {
        public IDb GetColumnDb(T key) => new ForwardOnlyDb(inner.GetColumnDb(key));
        public IColumnsWriteBatch<T> StartWriteBatch() => inner.StartWriteBatch();
        public IEnumerable<T> ColumnKeys => inner.ColumnKeys;
        public IColumnDbSnapshot<T> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void SyncWal() => inner.SyncWal();
        public void Dispose() => inner.Dispose();
    }

    private sealed class ForwardOnlyDb(IDb inner) : IDb, ISortedKeyValueStore
    {
        private ISortedKeyValueStore Sorted => (ISortedKeyValueStore)inner;
        public byte[]? FirstKey => Sorted.FirstKey;
        public byte[]? LastKey => Sorted.LastKey;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            new ForwardOnlyView(Sorted.GetViewBetween(firstKeyInclusive, lastKeyExclusive, flags));
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);
        public void Set(scoped ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => inner.Set(key, value, flags);
        public string Name => inner.Name;
        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => inner[keys];
        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => inner.GetAll(ordered);
        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => inner.GetAllKeys(ordered);
        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => inner.GetAllValues(ordered);
        public IWriteBatch StartWriteBatch() => inner.StartWriteBatch();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() { }
    }

    private sealed class ForwardOnlyView(ISortedView inner) : ISortedView
    {
        public bool StartBefore(ReadOnlySpan<byte> value) => inner.StartBefore(value);
        public bool MoveNext() => inner.MoveNext();
        public ReadOnlySpan<byte> CurrentKey => inner.CurrentKey;
        public ReadOnlySpan<byte> CurrentValue => inner.CurrentValue;
        public void Dispose() => inner.Dispose();
    }

    private sealed class ObservedDb(IDb inner, bool disposeInner = false) : IDb
    {
        private int _leases;
        public int Leases => Volatile.Read(ref _leases);
        public Action? BeforeSpan { get; set; }
        public Action<byte[]>? OnSpanKey { get; set; }
        public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Span<byte> value = inner.GetSpan(key, flags);
            Interlocked.Increment(ref _leases);
            try { OnSpanKey?.Invoke(key.ToArray()); BeforeSpan?.Invoke(); return value; }
            catch { ((IReadOnlyKeyValueStore)this).DangerousReleaseMemory(value); throw; }
        }
        void IReadOnlyKeyValueStore.DangerousReleaseMemory(in ReadOnlySpan<byte> value)
        {
            try { inner.DangerousReleaseMemory(value); }
            finally { Interlocked.Decrement(ref _leases); }
        }
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);
        public void Set(scoped ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => inner.Set(key, value, flags);
        public string Name => inner.Name;
        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => inner[keys];
        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => inner.GetAll(ordered);
        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => inner.GetAllKeys(ordered);
        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => inner.GetAllValues(ordered);
        public IWriteBatch StartWriteBatch() => inner.StartWriteBatch();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() { if (disposeInner) inner.Dispose(); }
    }

    private sealed class ControlledColumns(IColumnsDb<LogIndexColumns> inner) : IColumnsDb<LogIndexColumns>
    {
        public ObservedDb Meta { get; } = new(inner.GetColumnDb(LogIndexColumns.Meta));
        public Action? BeforeSync { get; set; }
        public bool FailHintWrite { get; set; }
        public IDb GetColumnDb(LogIndexColumns key) => key == LogIndexColumns.Meta ? Meta : inner.GetColumnDb(key);
        public IColumnsWriteBatch<LogIndexColumns> StartWriteBatch() => new ControlledBatch(inner.StartWriteBatch(), () => FailHintWrite);
        public IEnumerable<LogIndexColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnDbSnapshot<LogIndexColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void SyncWal() { BeforeSync?.Invoke(); inner.SyncWal(); }
        public void Dispose() => inner.Dispose();
    }

    private sealed class ControlledBatch(IColumnsWriteBatch<LogIndexColumns> inner, Func<bool> failHint) : IColumnsWriteBatch<LogIndexColumns>
    {
        public IWriteBatch GetColumnBatch(LogIndexColumns key) => key == LogIndexColumns.Meta
            ? new HintBatch(inner.GetColumnBatch(key), failHint) : inner.GetColumnBatch(key);
        public void Clear() => inner.Clear();
        public void Dispose() => inner.Dispose();
    }

    private sealed class HintBatch(IWriteBatch inner, Func<bool> failHint) : IWriteBatch
    {
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (failHint() && key.SequenceEqual(TracePositionSidecar.ProgressKey)) throw new IOException("Injected hint staging failure");
            inner.Set(key, value, flags);
        }
        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => inner.Merge(key, value, flags);
        public void Clear() => inner.Clear();
        public void Dispose() => inner.Dispose();
    }

    private sealed class IndexEnvironment : IDbFactory, IDisposable
    {
        private readonly TempPath _path = TempPath.GetTempDirectory();
        private readonly HyperClockCacheWrapper _cache = new(4_000_000);
        private readonly RocksDbFactory _factory;
        private readonly bool _reverseViews;
        private readonly bool _observe;
        public ControlledColumns? Controlled { get; private set; }
        public string Path => _path.Path;
        public IColumnsDb<LogIndexColumns>? Database { get; private set; }
        public DbSettings? Settings { get; private set; }
        public IndexEnvironment(bool reverseViews = true, bool observe = false)
        {
            _reverseViews = reverseViews;
            _observe = observe;
            DbConfig config = new();
            RocksDbConfigFactory rocksConfig = new(config, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
            _factory = new(rocksConfig, config, _cache, LimboLogs.Instance, _path.Path);
        }
        public IDb CreateDb(DbSettings settings) => _factory.CreateDb(settings);
        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings settings) where T : struct, Enum
        {
            Settings = settings;
            IColumnsDb<T> db = _factory.CreateColumnsDb<T>(settings);
            if (_observe)
            {
                Controlled = new((IColumnsDb<LogIndexColumns>)(object)db);
                db = (IColumnsDb<T>)(object)Controlled;
            }
            if (!_reverseViews) db = new ForwardOnlyColumns<T>(db);
            Database = (IColumnsDb<LogIndexColumns>)(object)db;
            return db;
        }
        public void Dispose()
        {
            _cache.Dispose();
            _path.Dispose();
        }
    }
}
