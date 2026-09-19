// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
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
    public async Task DebugAndTraceFactories_UseTheSameExecutionBudget()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        ExecutionCount counter = new();
        JsonRpcConfig rpc = new() { EnableTracingStreamMode = false, Timeout = 100 };
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithConfig(rpc)
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds)
                .AddSingleton(new ParallelTraceBudget(2))
                .AddDecorator<ITransactionProcessorAdapter>((_, inner) => new BudgetCountingAdapter(inner, counter)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        using (TransactionChangesetIndex.BlockCapture capture = index.StartBlock((ulong)block.Number))
        {
            using IDisposable scope = chain.MainWorldState.BeginScope(parent);
            chain.BlockProcessor.ProcessOne(block, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList,
                capture.Tracer, Prague.Instance, CancellationToken.None);
            Assert.That(capture.Commit() && index.TryClaim((ulong)block.Number, (ulong)block.Number), Is.True);
        }

        ParallelTraceBudget budget = chain.Container.Resolve<ParallelTraceBudget>();
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
            Assert.That(counter.Calls, Is.GreaterThanOrEqualTo(6));
            Assert.That(counter.Peak, Is.InRange(1, 2), "debug and trace share an aggregate execution limit");
        }

        Task<string[]> RunBoth() => Task.WhenAll(
            Task.Run(() => RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash, new { tracer = "callTracer" })),
            Task.Run(() => RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_block", block.Hash)));
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
