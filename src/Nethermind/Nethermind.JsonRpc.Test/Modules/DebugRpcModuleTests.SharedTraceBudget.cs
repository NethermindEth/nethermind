// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
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
using Nethermind.Evm.State;
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
    private static IEnumerable<TestCaseData> BufferedDeadlineRoutes()
    {
        foreach (bool indexed in new[] { false, true })
            foreach (bool filtered in new[] { false, true })
                foreach (string expiry in new[] { "reset", "before", "after" })
                    yield return new(indexed, filtered, expiry);
        yield return new(true, false, "external");
    }

    [TestCaseSource(nameof(BufferedDeadlineRoutes))]
    public async Task Buffered_deadline_runs_through_seeded_parallel_and_filtered_execution(bool indexed, bool filtered, string expiry)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        DeadlineExecution clock = new();
        ParallelDeadlineProbe probe = new();
        using ParallelTraceBudget budget = new(2);
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds)
                .AddSingleton(budget)
                .AddSingleton<ParallelTraceBudgets, ISpecProvider>(specProvider => new ParallelTraceBudgets(specProvider, budget, changesetSeeds: true))
                .AddDecorator<IParallelBlockTracer>((_, inner) => new DeadlineParallelTracer(inner, probe))
                .AddDecorator<ITransactionProcessorAdapter>((_, inner) => new DeadlineAdapter(inner, clock)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        if (indexed) IndexThroughTheCapture(chain, index, block, parent);
        clock.Enabled = true;
        clock.Target = nonce + (filtered ? 2UL : 1UL);
        clock.Prefix = filtered;
        clock.Expiry = expiry;
        clock.Cancel = () => probe.Cancel!();
        GethTraceOptions options = new()
        {
            Tracer = "callTracer",
            Timeout = TimeSpan.FromSeconds(1),
            DeadlineClock = clock,
            TxHash = filtered ? block.Transactions[2].Hash : null
        };
        // Keep parallel routing enabled but serialize execution so fake-clock advancement is deterministic.
        budget.Wait(CancellationToken.None);
        ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> result;
        try
        {
            if (expiry == "external")
            {
                Assert.Catch<OperationCanceledException>(() => chain.DebugRpcModule.debug_traceBlockByNumber(new BlockParameter(block.Number), options));
                Assert.That(probe.Attempts, Is.EqualTo(1));
                Assert.That(clock.Calls, Is.InRange(2, 3), "indexed workers may acquire the execution permit out of transaction order");
                Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
                return;
            }
            result = chain.DebugRpcModule.debug_traceBlockByNumber(new BlockParameter(block.Number), options);
        }
        finally { budget.Release(); }
        using ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> response = result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.Attempts, Is.EqualTo(filtered ? 0 : 1), "the full-block RPC must reach the production parallel route");
            Assert.That(probe.Completed, Is.EqualTo(indexed && !filtered && expiry == "reset" ? 1 : 0), "a successful seeded trace must not silently fall back");
            if (expiry == "reset")
            {
                Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Success), response.Result.Error);
                Assert.That(response.Data.Count, Is.EqualTo(filtered ? 1 : 3));
                Assert.That(clock.Calls, Is.EqualTo(indexed && filtered ? 1 : 3), "indexed filtering executes only its target; fallback replays the prefix outside the budget");
            }
            else
            {
                Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput));
                Assert.That(response.Result.Error, Is.EqualTo("execution timeout"));
                Assert.That(clock.Calls, indexed && !filtered ? Is.InRange(2, 3) : Is.EqualTo(filtered ? indexed ? 1 : 3 : 2),
                    "indexed workers may acquire the execution permit out of transaction order");
            }
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    private sealed class ParallelDeadlineProbe
    {
        internal int Attempts;
        internal int Completed;
        internal Action? Cancel;
    }

    private sealed class DeadlineParallelTracer(IParallelBlockTracer inner, ParallelDeadlineProbe probe) : IParallelBlockTracer
    {
        public bool TryTrace<TTrace>(Block block, BlockHeader parent, Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
            Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions, CancellationToken token, [NotNullWhen(true)] out IReadOnlyList<TTrace>? traces)
        {
            probe.Attempts++;
            using CancellationTokenSource external = CancellationTokenSource.CreateLinkedTokenSource(token);
            probe.Cancel = external.Cancel;
            bool completed = inner.TryTrace(block, parent, forTransaction, afterTransactions, external.Token, out traces);
            if (completed) probe.Completed++;
            return completed;
        }
        public bool TryStream<TTrace>(Block block, BlockHeader parent, Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
            Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions, Action<IReadOnlyCollection<TTrace>> emit, CancellationToken token) =>
            inner.TryStream(block, parent, forTransaction, afterTransactions, emit, token);
    }

    private sealed class DeadlineAdapter(ITransactionProcessorAdapter inner, DeadlineExecution clock) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer tracer)
        {
            if (!clock.Enabled) return inner.Execute(transaction, tracer);
            clock.Calls++;
            if (clock.Expiry != "after") clock.Advance(transaction.Nonce);
            TransactionResult result = inner.Execute(transaction, tracer);
            if (clock.Expiry == "after") clock.Advance(transaction.Nonce);
            return result;
        }
        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(context);
        public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) => inner.PrepareForInclusionCheck(transaction, stateGasAvailable);
    }

    private sealed class DeadlineExecution : TimeProvider
    {
        private long _timestamp;
        internal bool Enabled;
        internal bool Prefix;
        internal ulong Target;
        internal string Expiry = "reset";
        internal Action? Cancel;
        internal int Calls;
        internal List<ExecutionTimer> Timers { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ExecutionTimer timer = new(callback, state);
            Timers.Add(timer);
            return timer;
        }
        internal void Advance(ulong nonce)
        {
            if (Expiry == "external" && nonce == Target) Cancel!();
            _timestamp += TimeSpan.FromMilliseconds(Prefix && nonce < Target ? 10_000 : Expiry is "before" or "after" && nonce == Target ? 1_200 : 600).Ticks;
            foreach (ExecutionTimer timer in Timers) timer.Fire();
        }
    }

    private sealed class ExecutionTimer(TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        internal void Fire() { if (!Disposed) callback(state); }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    [Test]
    public async Task Debug_traceCall_log_indices_include_prefix_transactions([Values] bool revertFirst)
    {
        string[] responses = await TraceLogsBeforeAndAfterIndexing(revertFirst, (chain, block) =>
            RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceCall",
                new { from = TestItem.AddressB.ToString(), input = block.Transactions[2].Data.ToArray().ToHexString(true), gas = "0x186a0" },
                block.Hash!, new { tracer = "callTracer", tracerConfig = new { withLog = true }, txIndex = "0x2" }));

        AssertLastTransactionLogIndex(responses, revertFirst);
    }


    [Test]
    public async Task Debug_traceTransaction_log_indices_include_preceding_transactions([Values] bool revertFirst, [Values(true, "true")] object withLog)
    {
        string[] responses = await TraceLogsBeforeAndAfterIndexing(revertFirst, (chain, block) =>
            RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceTransaction", block.Transactions[2].Hash!,
                new { tracer = "callTracer", tracerConfig = new { withLog } }));

        AssertLastTransactionLogIndex(responses, revertFirst);
    }

    [Test]
    public async Task Debug_traceBlock_log_indices_span_transactions(
        [Values("debug_traceBlockByHash", "debug_traceBlockByNumber", "debug_traceBlock")] string method,
        [Values] bool revertFirst, [Values(true, "true")] object withLog)
    {
        string[] responses = await TraceLogsBeforeAndAfterIndexing(revertFirst, (chain, block) =>
        {
            object blockParameter = method switch
            {
                "debug_traceBlockByHash" => block.Hash!,
                "debug_traceBlockByNumber" => "latest",
                "debug_traceBlock" => Nethermind.Serialization.Rlp.Rlp.Encode(block).ToString(),
                _ => throw new AssertionException($"Unexpected block tracing method: {method}")
            };
            return RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, blockParameter,
                new { tracer = "callTracer", tracerConfig = new { withLog } });
        });

        foreach (string response in responses)
        {
            JToken json = JToken.Parse(response);
            Assert.That(json["error"], Is.Null, response);
            JArray traces = (JArray)json["result"]!;
            Assert.That(traces, Has.Count.EqualTo(3));
            using (Assert.EnterMultipleScope())
            {
                if (revertFirst)
                    Assert.That(traces[0]["result"]?["logs"], Is.Null, response);
                for (int i = revertFirst ? 1 : 0; i < traces.Count; i++)
                    Assert.That((string?)traces[i]["result"]?["logs"]?[0]?["index"], Is.EqualTo($"0x{i - (revertFirst ? 1 : 0):x}"), response);
            }
        }
    }

    private static void AssertLastTransactionLogIndex(string[] responses, bool revertFirst)
    {
        foreach (string response in responses)
        {
            JToken json = JToken.Parse(response);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(json["error"], Is.Null, response);
                Assert.That((string?)json["result"]?["logs"]?[0]?["index"], Is.EqualTo(revertFirst ? "0x1" : "0x2"), response);
            }
        }
    }

    private static async Task<string[]> TraceLogsBeforeAndAfterIndexing(
        bool revertFirst, Func<TestRpcBlockchain, Block, Task<string>> trace)
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
        string replayed = await trace(chain, block);
        IndexThroughTheCapture(chain, index, block, parent);
        string indexed = await trace(chain, block);
        return [replayed, indexed];
    }

    [Test]
    public async Task Debug_traceCall_and_traceCallMany_reject_malformed_timeout_before_execution(
        [Values("debug_traceCall", "debug_traceCallMany")] string method)
    {
        ExecutionCount counter = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddDecorator<ITransactionProcessorAdapter>((_, inner) => new BudgetCountingAdapter(inner, counter)));
        counter.Enabled = true;
        object transaction = new { from = TestItem.AddressB, to = TestItem.AddressC, gas = "0x186a0" };
        object input = method == "debug_traceCall" ? transaction : new[] { new { transactions = new[] { transaction } } };
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, input, "latest",
            new { timeout = "bad", streamMode = true });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"invalid argument 2\"},\"id\":67}"));
            Assert.That(counter.Calls, Is.Zero);
        }
    }

    [Test]
    public async Task Debug_traceTransaction_applies_seed_before_timeout_validation_and_disarms([Values] bool invalidTimeout)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ObservedPrefixSeeds seeds = new(new ChangesetPrefixStateSeedSource(index));
        ExecutionCount counter = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds)
                .AddDecorator<ITransactionProcessorAdapter>((_, inner) => new BudgetCountingAdapter(inner, counter)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        IndexThroughTheCapture(chain, index, block, parent);
        counter.Enabled = true;
        counter.OnExecute = transaction => Assert.That(transaction.Hash, Is.EqualTo(transactions[1].Hash), "only the seeded target may execute");
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceTransaction", transactions[1].Hash!,
            new { timeout = invalidTimeout ? "bad" : "5s", streamMode = true });
        JToken parsed = JToken.Parse(response);
        Assert.That(seeds.Slot, Is.Not.Null, "the real indexed source must have been called");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeds.Accepted, Is.EqualTo(1), "real changeset lease armed before timeout resolution");
            Assert.That(seeds.Index, Is.EqualTo(1));
            Assert.That(seeds.BlockHash, Is.EqualTo(block.Hash));
            Assert.That(seeds.Slot!.Current, Is.Null, "failure and success must both disarm and release the overlay lease");
            Assert.That(counter.Calls, Is.EqualTo(invalidTimeout ? 0 : 1), "refused application would replay the prefix instead");
            if (invalidTimeout)
            {
                Assert.That(parsed["result"], Is.Null);
                Assert.That(parsed["error"]?["code"]?.Value<int>(), Is.EqualTo(-32000));
                Assert.That(parsed["error"]?["message"]?.Value<string>(), Does.Contain("Cannot parse 'bad'"));
            }
            else
            {
                Assert.That(parsed["error"], Is.Null);
                Assert.That(parsed["result"]?["failed"]?.Value<bool>(), Is.False);
                Assert.That(parsed["result"]?["gas"]?.Value<int>(), Is.EqualTo(21_000));
            }
        }
    }

    private sealed class ObservedPrefixSeeds(IPrefixStateSeedSource inner) : IPrefixStateSeedSource
    {
        public bool Enabled => inner.Enabled;
        public StateReadOverlaySlot? Slot;
        public int Accepted;
        public int Index;
        public Hash256? BlockHash;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot)
        {
            Slot = slot;
            Index = transactionIndex;
            BlockHash = block.Hash;
            bool accepted = inner.TrySeed(block, transactionIndex, slot);
            if (accepted)
            {
                Assert.That(slot.Current, Is.Not.Null);
                Accepted++;
            }
            return accepted;
        }

        public bool TryOpenBlock(Block block, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ICoveredBlock? covered) =>
            inner.TryOpenBlock(block, out covered);
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
                .AddSingleton<ParallelTraceBudgets, ISpecProvider>(specProvider => new ParallelTraceBudgets(specProvider, budget, changesetSeeds: true))
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

    [TestCase("trace_block", null)]
    [TestCase("trace_filter", null)]
    [TestCase("trace_replayBlockTransactions", "trace")]
    [TestCase("trace_replayBlockTransactions", "trace,stateDiff")]
    [TestCase("trace_replayBlockTransactions", "trace,vmTrace,stateDiff")]
    [TestCase("trace_replayBlockTransactions", "stateDiff")]
    public async Task ParityTraces_OnAnIndexedBlock_StreamLikeTheReplay(string method, string? traceTypes)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        OpenedBlockCount seeds = new(new ChangesetPrefixStateSeedSource(index));
        using ParallelTraceBudget budget = new(2);
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = true })
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds)
                .AddSingleton(budget)
                .AddSingleton<ParallelTraceBudgets, ISpecProvider>(specProvider => new ParallelTraceBudgets(specProvider, budget, changesetSeeds: true)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        byte[][] codes =
        [
            Prepare.EvmCode.CallWithValue(TestItem.AddressC, 5_000, 1).Call(TestItem.AddressD, 5_000).Log(0, 0).STOP().Done,
            Prepare.EvmCode.Call(TestItem.AddressC, 5_000).Revert(0, 0).Done,
            Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf(Prepare.EvmCode.Op(Instruction.STOP).Done).Done, 0).STOP().Done,
            Prepare.EvmCode.Call(TestItem.AddressC, 5_000).Op(Instruction.INVALID).Done,
            Prepare.EvmCode.PersistData("0x01", "0x02").PushData(TestItem.AddressC).Op(Instruction.SELFDESTRUCT).Done,
            Prepare.EvmCode.CallWithValue(TestItem.AddressE, 5_000, 1_000).STOP().Done,
        ];
        // More transactions than workers, in two rounds, so each recycled tracer meets every kind of frame.
        Transaction[] transactions = new Transaction[2 * (codes.Length + 1)];
        for (int i = 0; i < transactions.Length; i++)
        {
            int kind = i % (codes.Length + 1);
            TransactionBuilder<Transaction> tx = kind == codes.Length
                ? Build.A.Transaction.WithTo(TestItem.AddressC)
                : Build.A.Transaction.WithCode(codes[kind]);
            transactions[i] = tx.WithValue(kind == codes.Length - 1 ? 0 : 10).WithNonce(nonce + (ulong)i)
                .WithGasLimit(300_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(transactions.Length), "precondition: every transaction is in the block");

        object[] parameters = method switch
        {
            "trace_block" => [block.Hash!],
            "trace_filter" => [new { fromBlock = $"0x{block.Number:x}", toBlock = $"0x{block.Number:x}" }],
            _ => [block.Hash!, traceTypes!.Split(',')],
        };
        string replayed = await RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, parameters);
        Assert.That(seeds.Opened, Is.Zero, "precondition: before the block is indexed it is replayed");
        IndexThroughTheCapture(chain, index, block, parent);
        string first = await RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, parameters);
        string second = await RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, parameters);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeds.Opened, Is.EqualTo(2), "precondition: the indexed block is traced in parallel");
            Assert.That(JToken.Parse(replayed)["error"], Is.Null, replayed);
            Assert.That(second, Is.EqualTo(first));
            // The streamed replay writes a vmTrace operation's sub-trace ahead of its other fields.
            if (traceTypes?.Contains("vmTrace") == true)
                Assert.That(JToken.DeepEquals(JToken.Parse(first), JToken.Parse(replayed)), Is.True, first);
            else
                Assert.That(first, Is.EqualTo(replayed));
        }
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
                .AddSingleton<ParallelTraceBudgets, ISpecProvider>(specProvider => new ParallelTraceBudgets(specProvider, budget, changesetSeeds: true))
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

    [TestCase("debug_traceTransaction", 0)]
    [TestCase("debug_traceTransaction", 1)]
    [TestCase("debug_traceTransaction", 2)]
    [TestCase("debug_traceBlockByHash", 1)]
    public async Task JavaScript_transaction_context_preserves_the_block_index(string method, int txIndex)
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
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(21_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions.Length, Is.EqualTo(transactions.Length));
        Transaction target = block.Transactions[txIndex];
        bool traceTransaction = method == "debug_traceTransaction";
        GethTraceOptions options = new()
        {
            Tracer = "{fault:function(){},result:function(ctx){return {txIndex:ctx.txIndex,block:ctx.block}}}",
            TxHash = traceTransaction ? null : target.Hash
        };
        object parameter = traceTransaction ? target.Hash! : block.Hash!;
        string replayed = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, parameter, options);
        IndexThroughTheCapture(chain, index, block, parent);
        string indexed = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, parameter, options);

        foreach (string response in new[] { replayed, indexed })
        {
            JToken json = JToken.Parse(response);
            Assert.That(json["error"], Is.Null, response);
            JToken result = traceTransaction ? json["result"]! : json["result"]![0]!["result"]!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result["txIndex"]!.Value<int>(), Is.EqualTo(txIndex), response);
                Assert.That(result["block"]!.Value<long>(), Is.EqualTo(block.Number));
            }
        }
    }

    public enum ReceiptAvailability { Stored, Missing, Truncated, Unreproducible }

    // Without usable receipts both paths number the logs from the body they replay, as the receipts would.
    [Test]
    public async Task Debug_traceBlockByHash_callTracer_withLog_OnAnIndexedBlock_ResolvesTheReceiptsOnce([Values] ReceiptAvailability availability, [Values] bool streamMode)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using ParallelTraceBudget budget = new(2);
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds)
                .AddSingleton(budget)
                .AddSingleton<ParallelTraceBudgets, ISpecProvider>(specProvider => new ParallelTraceBudgets(specProvider, budget, changesetSeeds: true))
                .AddKeyedSingleton<IReceiptFinder>(IReceiptFinder.RegenerableKey, ctx => new CountingReceiptFinder(ctx.Resolve<IReceiptFinder>(), availability)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithCode(Prepare.EvmCode.Log(0, 0).STOP().Done).WithNonce(nonce + (ulong)i)
                .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        IndexThroughTheCapture(chain, index, block, parent);
        CountingReceiptFinder receipts = (CountingReceiptFinder)chain.Container.ResolveKeyed<IReceiptFinder>(IReceiptFinder.RegenerableKey);

        int before = receipts.BlockGets;
        int iteratorsBefore = receipts.IteratorReads;
        GethTraceOptions options = new() { Tracer = "callTracer", TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"withLog":true}"""), StreamMode = streamMode };
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash, options);
        int blockTraceGets = receipts.BlockGets - before;
        int blockTraceIterators = receipts.IteratorReads - iteratorsBefore;
        string single = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceTransaction", transactions[^1].Hash, options);

        JArray result = (JArray)JToken.Parse(response)["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(transactions.Length), response);
            for (int i = 0; i < result.Count; i++)
                Assert.That((string)result[i]!["result"]!["logs"]![0]!["index"]!, Is.EqualTo($"0x{i:x}"), response);
            Assert.That((string)JToken.Parse(single)["result"]!["logs"]![0]!["index"]!, Is.EqualTo($"0x{transactions.Length - 1:x}"), single);
            // Read once on the calling thread: the parallel workers share it, and the replay it falls back to reads none.
            // Stored receipts are counted without decoding them; only an iterator that cannot answer leads to the full read.
            Assert.That(blockTraceIterators, Is.EqualTo(1));
            Assert.That(blockTraceGets, Is.EqualTo(availability is ReceiptAvailability.Missing or ReceiptAvailability.Truncated ? 1 : 0));
        }
    }

    [Test]
    public async Task Debug_traceBlockByHash_NativeTracerFailingOnAnIndexedBlock_StreamsTheTracedPrefixAndASentinel()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        ChangesetPrefixStateSeedSource seeds = new(index);
        using ParallelTraceBudget budget = new(2);
        Hash256? failingTx = null;
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = true })
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(seeds)
                .AddSingleton(budget)
                .AddSingleton<ParallelTraceBudgets, ISpecProvider>(specProvider => new ParallelTraceBudgets(specProvider, budget, changesetSeeds: true))
                .AddDecorator<ITransactionProcessorAdapter>((_, inner) => new FailingAdapter(inner, () => failingTx)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(parent, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transactions);
        IndexThroughTheCapture(chain, index, block, parent);
        failingTx = transactions[1].Hash;

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash, new GethTraceOptions { Tracer = "callTracer" });

        JToken parsed = JToken.Parse(response);
        JArray result = (JArray)parsed["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed["error"], Is.Null, "the failure comes after the stream opened, so it cannot be a top-level error");
            Assert.That(result, Has.Count.InRange(1, transactions.Length), response);
            for (int i = 0; i < result.Count - 1; i++)
                Assert.That((string)result[i]!["txHash"]!, Is.EqualTo(transactions[i].Hash!.ToString()), "entries before the failure are the block's prefix, in order");
            Assert.That((string)result[result.Count - 1]!["error"]!, Does.Contain("simulated mid-block failure"), response);
        }
    }

    private sealed class FailingAdapter(ITransactionProcessorAdapter inner, Func<Hash256?> failingTx) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
            transaction.Hash is not null && transaction.Hash == failingTx()
                ? throw new InvalidOperationException("simulated mid-block failure")
                : inner.Execute(transaction, txTracer);

        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(context);
        public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) => inner.PrepareForInclusionCheck(transaction, stateGasAvailable);
    }

    private sealed class CountingReceiptFinder(IReceiptFinder inner, ReceiptAvailability availability) : IReceiptFinder
    {
        private int _blockGets;
        private int _iteratorReads;

        public int BlockGets => Volatile.Read(ref _blockGets);

        public int IteratorReads => Volatile.Read(ref _iteratorReads);

        public Hash256? FindBlockHash(Hash256 txHash) => inner.FindBlockHash(txHash);

        public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true)
        {
            Interlocked.Increment(ref _blockGets);
            return availability switch
            {
                ReceiptAvailability.Missing => [],
                ReceiptAvailability.Unreproducible => throw new ResourceNotFoundException($"No receipts for block {block.Number}"),
                _ => inner.Get(block, recover, recoverSender)
            };
        }

        public TxReceipt[] Get(Hash256 blockHash, bool recover = true) => inner.Get(blockHash, recover);

        public bool CanGetReceiptsByHash(ulong blockNumber) => inner.CanGetReceiptsByHash(blockNumber);

        public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            Interlocked.Increment(ref _iteratorReads);
            switch (availability)
            {
                case ReceiptAvailability.Missing:
                    iterator = default;
                    return false;
                case ReceiptAvailability.Unreproducible:
                    throw new ResourceNotFoundException($"No receipts for block {blockNumber}");
                case ReceiptAvailability.Truncated:
                    // Fewer stored receipts than transactions: the count cannot be trusted, so the full read decides.
                    iterator = new ReceiptsIterator(inner.Get(blockHash)[..^1]);
                    return true;
                default:
                    return inner.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);
            }
        }
    }

    internal static void IndexThroughTheCapture(TestRpcBlockchain chain, TransactionChangesetIndex index, Block block, BlockHeader parent)
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
        public Action<Transaction>? OnExecute;
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
            try
            {
                count.OnExecute?.Invoke(transaction);
                return inner.Execute(transaction, txTracer);
            }
            finally { lock (count.Gate) count.Active--; }
        }

        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(context);
        public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) => inner.PrepareForInclusionCheck(transaction, stateGasAvailable);
    }

    private sealed class OpenedBlockCount(IPrefixStateSeedSource inner) : IPrefixStateSeedSource
    {
        public int Opened;

        public bool Enabled => inner.Enabled;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot) => inner.TrySeed(block, transactionIndex, slot);

        public bool TryOpenBlock(Block block, [NotNullWhen(true)] out ICoveredBlock? covered)
        {
            if (!inner.TryOpenBlock(block, out covered)) return false;
            Interlocked.Increment(ref Opened);
            return true;
        }
    }
}
