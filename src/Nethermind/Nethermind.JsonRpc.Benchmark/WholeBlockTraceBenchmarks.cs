// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;
using Perfolizer.Horology;

namespace Nethermind.JsonRpc.Benchmark;

/// <summary>Compares full-block trace execution using an in-memory parent state and captured transaction changesets.</summary>
/// <remarks>Measures replay preparation and execution costs. Historical database I/O and HTTP serialization are excluded.</remarks>
[Config(typeof(TraceBlockConfig))]
[MemoryDiagnoser]
[MedianColumn]
[JsonExporterAttribute.FullCompressed]
public class WholeBlockTraceBenchmarks
{
    private const int HeavyTransactions = 16;
    private const int HeavyCalls = 256;
    private static readonly Address StorageContract = new("0x00000000000000000000000000000000000000d1");
    private static readonly Address RouterContract = new("0x00000000000000000000000000000000000000d2");
    private static readonly Address HeavyRouterContract = new("0x00000000000000000000000000000000000000d3");
    private static readonly GethTraceOptions TraceOptions = new() { Tracer = "callTracer" };

    private BasicTestBlockchain _chain = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetIndex _index = null!;
    private ChangesetPrefixStateSeedSource _seeds = null!;
    private Block _block = null!;
    private BlockHeader _parent = null!;
    private ParallelTraceBudget _budget = null!;
    private ParallelBlockTracer _parallel = null!;
    private ParallelBlockTracer.OwnedEnvironment _sequential = null!;
    private Func<IWorldState, IBlockTracer<GethLikeTxTrace>> _forBlock = null!;
    private Func<IWorldState, Hash256, IBlockTracer<GethLikeTxTrace>> _forTransaction = null!;

    [Params(32, 128, 300)]
    public int Transactions { get; set; }

    [Params(2, 4)]
    public int Workers { get; set; }

    [Params(WorkloadShape.Uniform, WorkloadShape.Clustered, WorkloadShape.Dispersed)]
    public WorkloadShape Shape { get; set; }

    public enum WorkloadShape { Uniform, Clustered, Dispersed }

    [GlobalSetup]
    public async Task Setup()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        _seeds = new ChangesetPrefixStateSeedSource(_index);
        _chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
            .AddSingleton<IBlockValidationModule, PrefixReplayValidationModule>()
            .AddSingleton<IPrefixStateSeedSource>(_seeds)
            .WithGenesisPostProcessor((block, state) =>
            {
                block.Header.GasLimit = 30_000_000;
                state.CreateAccount(StorageContract, 0);
                state.InsertCode(StorageContract, StorageCode(), Prague.Instance);
                state.CreateAccount(RouterContract, 0);
                state.InsertCode(RouterContract, RouterCode(1), Prague.Instance);
                state.CreateAccount(HeavyRouterContract, 0);
                state.InsertCode(HeavyRouterContract, RouterCode(HeavyCalls), Prague.Instance);
            }));

        _parent = _chain.BlockTree.Head!.Header;
        Transaction[] transactions = new Transaction[Transactions];
        int heavyCount = 0;
        int heavyReverts = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            bool heavy = IsHeavy(i);
            if (heavy) heavyCount++;
            if (heavy && i % 4 == 3) heavyReverts++;
            transactions[i] = Build.A.Transaction.WithTo(heavy ? HeavyRouterContract : RouterContract).WithNonce((ulong)i)
                .WithData(i % 4 == 3 ? [1] : []).WithGasLimit(heavy ? 1_000_000ul : 100_000ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }
        if (heavyCount != (Shape == WorkloadShape.Uniform ? 0 : HeavyTransactions)
            || heavyReverts != (Shape == WorkloadShape.Uniform ? 0 : HeavyTransactions / 4))
            throw new InvalidOperationException("The heavy workloads must contain the same call and revert counts.");

        _block = await _chain.AddBlock(transactions);
        if (_block.Transactions.Length != Transactions)
            throw new InvalidOperationException("The benchmark block omitted transactions.");
        TxReceipt[] receipts = _chain.ReceiptStorage.Get(_block);
        if (receipts.Length != Transactions || _block.GasUsed <= (ulong)Transactions * 21_000 || _block.GasUsed >= 30_000_000)
            throw new InvalidOperationException("The benchmark block did not execute its contract calls.");
        for (int i = 0; i < receipts.Length; i++)
        {
            bool succeeds = i % 4 != 3;
            int calls = IsHeavy(i) ? HeavyCalls : 1;
            if (receipts[i].StatusCode != (succeeds ? StatusCode.Success : StatusCode.Failure)
                || receipts[i].Logs?.Length != (succeeds ? calls : 0)
                || receipts[i].GasUsed >= transactions[i].GasLimit)
                throw new InvalidOperationException("The benchmark calls must commit dependent writes or revert them as configured.");
        }
        IndexBlock();

        _forBlock = state => GethStyleTracer.CreateOptionsTracer(_block.Header, TraceOptions, state, _chain.SpecProvider);
        _forTransaction = (state, hash) => GethStyleTracer.CreateOptionsTracer(
            _block.Header, TraceOptions with { TxHash = hash }, state, _chain.SpecProvider);
        _budget = new ParallelTraceBudget(Workers);
        _parallel = CreateParallelTracer();
        _sequential = BuildEnvironment();
        VerifyResults();
    }

    [Benchmark(Baseline = true)]
    public int Sequential()
    {
        using Scope<ParallelBlockTracer.Components> scope = _sequential.BuildAndOverride(_parent);
        IBlockTracer<GethLikeTxTrace> tracer = _forBlock(scope.Component.WorldState);
        ReplaySequential(scope.Component, tracer);
        return DisposeResults(tracer.BuildResult());
    }

    private void ReplaySequential(ParallelBlockTracer.Components components, IBlockTracer<GethLikeTxTrace> tracer) =>
        components.Processor.Process(_block.WithReplacedHeader(_block.Header.Clone()),
            TraceProcessingOptions.ReadOnlyReplay, tracer.WithCancellation(CancellationToken.None), CancellationToken.None);

    [Benchmark]
    public int IndexedPerTransaction()
    {
        if (!_parallel.TryTrace(_block, _parent, _forTransaction, null, CancellationToken.None,
                out IReadOnlyList<GethLikeTxTrace>? traces))
            throw new InvalidOperationException("The indexed trace path was not used.");
        return DisposeResults(traces);
    }

    [Benchmark]
    public int IndexedBatches()
    {
        if (!_parallel.TryTraceBatches(_block, _parent, _forBlock, null, CancellationToken.None,
                out IReadOnlyList<GethLikeTxTrace>? traces))
            throw new InvalidOperationException("The batched trace path was not used.");
        return DisposeResults(traces);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _parallel?.Dispose();
        _sequential?.Dispose();
        _budget?.Dispose();
        _chain?.Dispose();
        _columns?.Dispose();
    }

    private void IndexBlock()
    {
        using TransactionChangesetIndex.BlockCapture capture = _index.StartBlock(_block.Number);
        using IDisposable scope = _chain.MainWorldState.BeginScope(_parent);
        _chain.BlockProcessor.ProcessOne(_block, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList,
            capture.Tracer, Prague.Instance, CancellationToken.None);
        if (!capture.Commit() || !_index.TryClaim(_block.Number, _block.Number)
            || !_index.TryGetCoverage(out ulong from, out ulong to) || from > _block.Number || to < _block.Number
            || !_index.HasRowsOf(_block.Number, _block.Hash!))
            throw new InvalidOperationException("The benchmark block has no complete transaction-index coverage.");
    }

    private void VerifyResults()
    {
        ExecutionCounter counter = new();
        using ParallelBlockTracer.OwnedEnvironment sequentialValidation = BuildEnvironment(counter);
        string expected;
        using (Scope<ParallelBlockTracer.Components> scope = sequentialValidation.BuildAndOverride(_parent))
        {
            IBlockTracer<GethLikeTxTrace> tracer = _forBlock(scope.Component.WorldState);
            ReplaySequential(scope.Component, tracer);
            expected = SerializeAndDispose(tracer.BuildResult());
        }
        if (counter.Executions != Transactions || counter.CancelableExecutions != Transactions)
            throw new InvalidOperationException("The full-block control did not execute each transaction with a cancelable VM tracer.");

        counter.Reset();
        using ParallelBlockTracer validation = CreateParallelTracer(counter);
        if (!validation.TryTrace(_block, _parent, _forTransaction, null, CancellationToken.None,
                out IReadOnlyList<GethLikeTxTrace>? individual))
            throw new InvalidOperationException("The individual indexed control refused the captured block.");
        string individualJson = SerializeAndDispose(individual);
        if (individualJson != expected || counter.Executions != Transactions || counter.CancelableExecutions != Transactions)
            throw new InvalidOperationException("The individual indexed control differs from full-block replay, replayed a prefix, or used a noncancelable VM tracer.");

        counter.Reset();
        if (!validation.TryTraceBatches(_block, _parent, _forBlock, null, CancellationToken.None,
                out IReadOnlyList<GethLikeTxTrace>? batched))
            throw new InvalidOperationException("The batched indexed control refused the captured block.");
        string batchedJson = SerializeAndDispose(batched);
        if (batchedJson != expected || counter.Executions != Transactions || counter.CancelableExecutions != Transactions)
            throw new InvalidOperationException("The batched control differs from full-block replay, replayed a prefix, or used a noncancelable VM tracer.");
    }

    private string SerializeAndDispose(IReadOnlyCollection<GethLikeTxTrace> traces)
    {
        try
        {
            if (traces.Count != Transactions)
                throw new InvalidOperationException("A trace result omitted transactions.");
            int index = 0;
            foreach (GethLikeTxTrace trace in traces)
            {
                if (trace.TxHash != _block.Transactions[index].Hash)
                    throw new InvalidOperationException("A trace result changed transaction order.");
                int calls = IsHeavy(index) ? HeavyCalls : 1;
                if (trace.CustomTracerResult?.Value is not NativeCallTracerCallFrame frame
                    || frame.Calls.Count != calls || frame.Output.Length != 32
                    || (frame.Error is null) != (index % 4 != 3))
                    throw new InvalidOperationException("A trace did not execute the configured calls before returning or reverting.");
                foreach (NativeCallTracerCallFrame child in frame.Calls)
                {
                    if (child.To != StorageContract || child.Error is not null || child.Output.Length != 32)
                        throw new InvalidOperationException("A benchmark storage call failed or returned invalid output.");
                }
                index++;
            }
            return _chain.JsonSerializer.Serialize(new GethLikeTxTraceCollection(traces));
        }
        finally
        {
            DisposeResults(traces);
        }
    }

    private static int DisposeResults(IReadOnlyCollection<GethLikeTxTrace> traces)
    {
        int count = traces.Count;
        foreach (GethLikeTxTrace trace in traces) trace.Dispose();
        return count;
    }

    private ParallelBlockTracer CreateParallelTracer(ExecutionCounter? counter = null) =>
        new(() => BuildEnvironment(counter), _seeds, new ParallelTraceBudgets(_chain.SpecProvider, _budget, changesetSeeds: true), NullLogManager.Instance);

    private ParallelBlockTracer.OwnedEnvironment BuildEnvironment(ExecutionCounter? counter = null)
    {
        IBlockValidationModule[] validation = _chain.Container.Resolve<IBlockValidationModule[]>();
        IOverridableEnv env = _chain.Container.Resolve<ITraceEnvFactory>().CreateForTracing();
        ILifetimeScope scope = _chain.Container.BeginLifetimeScope(builder =>
        {
            builder.AddModule(validation).AddModule(new TransactionTraceModule(validation)).AddModule(env)
                .Add<ParallelBlockTracer.Components>();
            if (counter is not null)
                builder.AddDecorator<TransactionProcessorAdapterFactory>((_, inner) => processor => new CountingTransactionAdapter(inner(processor), counter));
        });
        return new ParallelBlockTracer.OwnedEnvironment(scope.Resolve<IOverridableEnv<ParallelBlockTracer.Components>>(), scope);
    }

    private static byte[] StorageCode() =>
    [
        0x60, 0x00, 0x54, 0x60, 0x01, 0x01, 0x80, 0x60, 0x00, 0x55,
        0x60, 0x00, 0x52, 0x60, 0x2a, 0x60, 0x00, 0x60, 0x00, 0xa1,
        0x36, 0x15, 0x60, 0x1e, 0x57, 0x60, 0x20, 0x60, 0x00, 0xfd,
        0x5b, 0x60, 0x20, 0x60, 0x00, 0xf3,
    ];

    private bool IsHeavy(int index)
    {
        if (Shape == WorkloadShape.Uniform) return false;
        if (Shape == WorkloadShape.Clustered) return index >= 1 && index <= HeavyTransactions;
        for (int i = 0; i < HeavyTransactions; i++)
        {
            int start = i * Transactions / HeavyTransactions;
            if (index == start - start % 4 + i % 4) return true;
        }
        return false;
    }

    private static byte[] RouterCode(int calls)
    {
        List<byte> code = [];
        List<int> failureJumps = [];
        for (int i = 0; i < calls; i++)
        {
            code.AddRange([0x60, 0x20, 0x60, 0x00, 0x60, 0x00, 0x60, 0x00, 0x60, 0x00, 0x73]);
            code.AddRange(StorageContract.Bytes);
            code.AddRange([0x5a, 0xf1, 0x15, 0x61]);
            failureJumps.Add(code.Count);
            code.AddRange([0x00, 0x00, 0x57]);
        }

        code.AddRange([0x36, 0x61]);
        failureJumps.Add(code.Count);
        code.AddRange([0x00, 0x00, 0x57, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        int failure = code.Count;
        code.AddRange([0x5b, 0x60, 0x20, 0x60, 0x00, 0xfd]);
        foreach (int jump in failureJumps)
        {
            code[jump] = checked((byte)(failure >> 8));
            code[jump + 1] = (byte)(failure & 0xff);
        }
        if (code.Count > 24_576)
            throw new InvalidOperationException("The benchmark router exceeds the contract code limit.");
        return code.ToArray();
    }

    private sealed class TraceBlockConfig : ManualConfig
    {
        public TraceBlockConfig() => AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithLaunchCount(1).WithWarmupCount(8).WithIterationCount(8)
            .WithIterationTime(TimeInterval.FromMilliseconds(250)).WithGcForce(true));
    }

    private sealed class PrefixReplayValidationModule : Module, IBlockValidationModule
    {
        public bool SupportsTransactionTracePrefix => true;

        protected override void Load(ContainerBuilder builder) => builder
            .AddScoped<IBlockProcessor, TransactionTraceBlockProcessor>().AddScoped<TransactionTraceExecutor>();
    }

    private sealed class ExecutionCounter
    {
        private int _executions;
        private int _cancelableExecutions;
        public int Executions => _executions;
        public int CancelableExecutions => _cancelableExecutions;

        public void Count(ITxTracer tracer)
        {
            Interlocked.Increment(ref _executions);
            if (tracer.IsCancelable) Interlocked.Increment(ref _cancelableExecutions);
        }

        public void Reset()
        {
            _executions = 0;
            _cancelableExecutions = 0;
        }
    }

    private sealed class CountingTransactionAdapter(ITransactionProcessorAdapter inner, ExecutionCounter counter) : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            counter.Count(txTracer);
            return inner.Execute(transaction, txTracer);
        }

        public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(context);
        public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) => inner.PrepareForInclusionCheck(transaction, stateGasAvailable);
    }
}
