// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Flat.History.Changesets;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using FlatHistoryColumns = Nethermind.State.Flat.FlatHistoryColumns;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_callTracer_log_indices_span_transactions(
        [Values("debug_traceTransaction", "debug_traceBlockByHash", "debug_traceBlockByNumber", "debug_traceBlock", "debug_traceCall")] string method,
        [Values] bool revertFirst)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
        {
            byte[] code = revertFirst && i == 0
                ? Prepare.EvmCode.Log(0, 0).Revert(0, 0).Done
                : Prepare.EvmCode.Log(0, 0).STOP().Done;
            transactions[i] = Build.A.Transaction.WithCode(code).WithNonce(nonce + (ulong)i)
                .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions.Length, Is.EqualTo(3));
        object blockParameter = method switch
        {
            "debug_traceTransaction" => block.Transactions[2].Hash!,
            "debug_traceBlockByNumber" => "latest",
            "debug_traceBlock" => Nethermind.Serialization.Rlp.Rlp.Encode(block).ToString(),
            _ => block.Hash!
        };
        object options = new { tracer = "callTracer", tracerConfig = new { withLog = true } };
        async Task<string> Trace() => method == "debug_traceCall"
            ? await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method,
                new { from = TestItem.AddressB.ToString(), input = transactions[2].Data.ToArray().ToHexString(true), gas = "0x186a0" },
                block.Hash!, new { tracer = "callTracer", tracerConfig = new { withLog = true }, txIndex = "0x2" })
            : await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, blockParameter, options);
        string replayed = await Trace();
        IndexThroughTheCapture(chain, index, block, parent);
        string indexed = await Trace();

        foreach (string response in new[] { replayed, indexed })
        {
            JToken json = JToken.Parse(response);
            Assert.That(json["error"], Is.Null, response);
            if (method is "debug_traceTransaction" or "debug_traceCall")
                Assert.That((string?)json["result"]?["logs"]?[0]?["index"], Is.EqualTo(revertFirst ? "0x1" : "0x2"), response);
            else
            {
                JArray traces = (JArray)json["result"]!;
                Assert.That(traces, Has.Count.EqualTo(3));
                for (int i = revertFirst ? 1 : 0; i < traces.Count; i++)
                    Assert.That((string?)traces[i]["result"]?["logs"]?[0]?["index"], Is.EqualTo($"0x{i - (revertFirst ? 1 : 0):x}"), response);
            }
        }
    }

    [Test]
    public async Task DebugAndTraceFactories_UseTheSameExecutionBudget()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        ExecutionCount counter = new();
        using ParallelTraceBudget budget = new(2);
        JsonRpcConfig rpc = new() { EnableTracingStreamMode = false, Timeout = 100 };
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithConfig(rpc)
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds)
                .AddSingleton(budget)
                .AddDecorator<ITransactionProcessorAdapter>((_, inner) => new BudgetCountingAdapter(inner, counter)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        IndexThroughTheCapture(chain, index, block, parent);

        Assert.That(chain.Container.Resolve<ParallelTraceBudget>(), Is.SameAs(budget));
        counter.Enabled = true;
        budget.Wait(CancellationToken.None);
        budget.Wait(CancellationToken.None);
        Task<string[]> blocked = RunBoth();
        try
        {
            string[] refused = await blocked.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(counter.Calls, Is.Zero, "both production factories must wait on the same saturated node budget");
                foreach (string response in refused) Assert.That(JToken.Parse(response)["error"], Is.Not.Null);
            }
        }
        finally
        {
            budget.Release();
            budget.Release();
            await blocked;
        }

        rpc.Timeout = 20_000;
        string[] completed = await RunBoth();
        using (Assert.EnterMultipleScope())
        {
            foreach (string response in completed) Assert.That(JToken.Parse(response)["error"], Is.Null);
            Assert.That(counter.Calls, Is.EqualTo(6), "three transactions per request, each executed once: a worker whose seed was refused would replay its prefix and show up here");
            Assert.That(counter.Peak, Is.InRange(1, 2), "debug and trace share an aggregate execution limit");
        }

        Task<string[]> RunBoth() => Task.WhenAll(
            Task.Run(() => RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash, new { tracer = "callTracer" })),
            Task.Run(() => RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_block", block.Hash)));
    }

    [Test]
    public async Task Debug_traceBlockByHash_WithATransactionFilter_ReturnsThatTransactionAlone_OnAnIndexedBlock()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        ExecutionCount counter = new() { Enabled = true };
        using ParallelTraceBudget budget = new(2);
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds)
                .AddSingleton(budget)
                .AddDecorator<ITransactionProcessorAdapter>((_, inner) => new BudgetCountingAdapter(inner, counter)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        Hash256 target = block.Transactions[1].Hash!;
        GethTraceOptions options = new() { Tracer = "callTracer", TxHash = target };

        counter.Calls = 0;
        string sequential = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash, options);
        int replayed = counter.Calls;
        IndexThroughTheCapture(chain, index, block, parent);
        counter.Calls = 0;
        string indexed = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash, options);
        string unfiltered = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash, new GethTraceOptions { Tracer = "callTracer" });

        JArray result = (JArray)JToken.Parse(indexed)["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(2), "precondition: before the block is indexed the filter is answered by replaying the block up to its target");
            Assert.That(indexed, Is.EqualTo(sequential), "the filter must survive the indexed path: the response is the sequential one");
            Assert.That(result, Has.Count.EqualTo(1), "one transaction was asked for");
            Assert.That(result[0]!["txHash"]!.Value<string>(), Is.EqualTo(target.ToString()));
            Assert.That((JArray)JToken.Parse(unfiltered)["result"]!, Has.Count.EqualTo(3), "precondition: without the filter the indexed block traces whole");
            Assert.That(counter.Calls, Is.EqualTo(1 + 3), "the filtered request took the seeded single-transaction path and executed its target alone; the whole-block request executed each transaction once");
        }
    }

    private static void IndexThroughTheCapture(TestRpcBlockchain chain, TransactionChangesetIndex index, Block block, BlockHeader parent)
    {
        using TransactionChangesetIndex.BlockCapture capture = index.StartBlock((ulong)block.Number);
        using IDisposable scope = chain.MainWorldState.BeginScope(parent);
        chain.BlockProcessor.ProcessOne(block, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList,
            capture.Tracer, Prague.Instance, CancellationToken.None);
        Assert.That(capture.Commit() && index.TryClaim((ulong)block.Number, (ulong)block.Number), Is.True, "precondition: the block must be indexed");
    }

    private sealed class ExecutionCount
    {
        public bool Enabled;
        public int Calls;
        public int Active;
        public int Peak;
        public readonly Lock Gate = new();
    }

    private sealed class BudgetCountingAdapter(ITransactionProcessorAdapter inner, ExecutionCount count) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            if (!count.Enabled) return inner.Execute(transaction, txTracer);
            lock (count.Gate)
            {
                count.Calls++;
                count.Peak = Math.Max(count.Peak, ++count.Active);
            }
            try { return inner.Execute(transaction, txTracer); }
            finally { lock (count.Gate) count.Active--; }
        }

        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(context);
        public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) => inner.PrepareForInclusionCheck(transaction, stateGasAvailable);
    }
}
