// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using Autofac;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Tracing;

/// <summary>Traces covered blocks concurrently and returns or emits results in transaction order.</summary>
/// <remarks>Runs the transactions of a covered block on as many processing environments as there are workers. Each
/// transaction is traced alone through the same seeded path a single-transaction trace takes: the worker's seed
/// source arms the writes of the transactions before it, only the target executes, and its trace is byte for byte
/// what the replay would have produced. Workers take transactions in ascending order off one cursor, so each folds
/// every changeset once. The first transaction runs on the calling thread before the workers start, which recovers
/// the block's senders once and proves the tracer can be bounded to one transaction; a tracer that cannot be sends
/// the caller back to the replay before any worker exists. The caller always waits for its workers, so nothing they
/// hold is released under them, even when the request is cancelled. The workers are threads of their own, started on
/// the first block and kept: they spend their time blocked in state reads, and a thread pool hands out threads for
/// blocked work too slowly for a block to ever see the whole degree. There are twice as many threads as the degree
/// and the degree is enforced per transaction by a semaphore, so two blocks traced at once interleave transaction by
/// transaction instead of the second waiting for the whole of the first. The calling thread is one of the workers.
/// Every block draws on the one node-wide budget, whichever seed it stands on, so the setting bounds all its workers.</remarks>
public sealed partial class ParallelBlockTracer : IParallelBlockTracer, IDisposable
{
    private readonly ShareableOverridableEnvSource<Components> _environments;
    private readonly IPrefixStateSeedSource _seeds;
    private readonly ParallelTraceBudgets _budgets;
    private readonly Workers _workers;
    private readonly SerializedTraces.Pool _serialized;
    private readonly int _degree;
    private readonly ILogger _logger;
    private readonly object _runs = new();

    /// <summary>Transactions per worker that a streamed block may have traced and not yet written, unless a test
    /// sets <see cref="StreamedPerWorker"/>.</summary>
    private const int DefaultStreamedPerWorker = 32;

    /// <summary>Bytes of one streamed block that may be serialized and not yet written, counted as the chunk memory
    /// they hold, at least <see cref="StreamedBudgetBytesPerWorker"/> per worker. With the window it bounds the memory a
    /// request holds when its client reads slowly: 32 MiB, and 4 MiB per worker above 8 workers. A worker that finds
    /// it spent waits before it takes another transaction.</summary>
    internal const long StreamedBudgetBytes = 32 * 1024 * 1024;

    /// <summary>The budget each worker adds, so that large traces still leave every worker room to trace ahead.</summary>
    internal const long StreamedBudgetBytesPerWorker = 4 * 1024 * 1024;

    /// <summary>Idle chunks kept for the next blocks, at least this many bytes of them; the store also covers one
    /// chunk for every transaction of the widest windows two interleaved blocks can hold.</summary>
    private const long RetainedSerializedBytes = 8 * 1024 * 1024;

    private bool _disposed;
    private int _inFlight;
    private IRewardCalculator? _rewardCalculator;

    /// <summary>Transactions per worker that a streamed block may have traced and not yet written: enough for the
    /// workers to pass a transaction that costs as much as many others, few enough that the traces waiting behind it
    /// stay bounded. The value is a heuristic.</summary>
    internal int StreamedPerWorker { get; init; } = DefaultStreamedPerWorker;

    internal bool IsDisposing
    {
        get { lock (_runs) return _disposed; }
    }

    /// <summary>Owns the supplied environments, but borrows the budgets. Dispose the tracer before the budgets.</summary>
    public ParallelBlockTracer(Func<IOverridableEnv<Components>> buildEnvironment, IPrefixStateSeedSource seeds, ParallelTraceBudgets budgets, ILogManager logManager)
    {
        _degree = budgets.Degree;
        _environments = new ShareableOverridableEnvSource<Components>(buildEnvironment, _degree);
        _budgets = budgets;
        _workers = new Workers(2 * _degree);
        // Every instance of the widest windows two interleaved blocks can hold goes back to the pool, with a chunk
        // each, so a backlog of small traces allocates none; larger traces past the store use the shared array pool.
        int widestWindows = 2 * _degree * DefaultStreamedPerWorker;
        _serialized = new SerializedTraces.Pool(widestWindows, Math.Max(widestWindows, (int)(RetainedSerializedBytes / SerializedTraces.ChunkSize)));
        _seeds = seeds;
        _logger = logManager.GetClassLogger<ParallelBlockTracer>();
    }

    /// <inheritdoc />
    public bool TryTrace<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        CancellationToken token,
        [NotNullWhen(true)] out IReadOnlyList<TTrace>? traces) =>
        Run(block, parent, forTransaction, afterTransactions, stream: null, token, out traces);

    /// <inheritdoc />
    public bool TryStream<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        IParallelTraceStream<TTrace> stream,
        CancellationToken token) =>
        Run(block, parent, forTransaction, afterTransactions, stream, token, out _);

    private bool Run<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        IParallelTraceStream<TTrace>? stream,
        CancellationToken token,
        out IReadOnlyList<TTrace>? traces)
    {
        traces = null;
        if (!_seeds.Enabled || block.Transactions.Length < 2 || !HashesKnown(block.Transactions)) return false;
        if (!_budgets.TryGetParallel(block.Header, out ParallelTraceBudget? slots)) return false;

        // Counted under the same lock disposal takes, so a tracer being disposed either sees this run and waits for
        // it or refuses it outright: the caller traces a share of the block on its own thread, and nothing it holds
        // may be disposed under it.
        if (!Enter()) return false;

        try
        {
            if (!_seeds.TryOpenBlock(block, out ICoveredBlock? covered)) return false;
            using ICoveredBlock coverage = covered;

            Transaction[] transactions = block.Transactions;
            int workers = Math.Max(1, Math.Min(slots.Degree, transactions.Length - 1));
            BlockRun<TTrace> run = BlockRun<TTrace>.Rent();
            try
            {
                ParallelTraceResults<TTrace> results = run.Start(this, block, parent, slots, forTransaction, stream, workers, token);
                try
                {
                    if (!run.Trace(covered)) return false;

                    // Only rewards are traced after the transactions, so a block that is never rewarded needs no
                    // replay, and no permit or environment either.
                    if (afterTransactions is not null && Volatile.Read(ref _rewardCalculator)?.MayReward(block.Header) != false)
                    {
                        results.Add(transactions.Length, TraceAfterTransactions(block, parent, slots, covered.CreateWorkerSeeds(), afterTransactions, token));
                    }

                    results.Complete();
                    covered.Complete();
                }
                catch
                {
                    // Every worker has returned by now: Trace let the failure out only once they had. What they
                    // finished and nobody received is this run's to release.
                    results.DisposeUnpublished();
                    throw;
                }

                traces = run.TakeTraces();
            }
            finally
            {
                run.Return();
            }

            if (_logger.IsTrace) _logger.Trace($"Traced block {block.Number} in parallel: {transactions.Length} transactions on {workers} workers.");
            return true;
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>One environment per permit of the budget, so no permit holder finds the pool exhausted.</summary>
    internal Scope<Components> RentEnvironment(BlockHeader parent) => _environments.BuildAndOverride(parent);

    private bool Enter()
    {
        lock (_runs)
        {
            if (_disposed) return false;

            _inFlight++;
            return true;
        }
    }

    private void Leave()
    {
        lock (_runs)
        {
            if (--_inFlight == 0) Monitor.PulseAll(_runs);
        }
    }

    /// <summary>False when the environment cannot seed or the tracer cannot be bounded; the caller replays the block.</summary>
    private bool TraceOne<TTrace>(Block block, BlockHeader parent, Transaction[] transactions, int index, ParallelTraceBudget slots, IPrefixStateSeedSource seeds,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction, ParallelTraceResults<TTrace> results, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Hash256 hash = transactions[index].Hash!;
        IReadOnlyCollection<TTrace>? traces = null;
        slots.Wait(token);
        try
        {
            using Scope<Components> scope = RentEnvironment(parent);
            // Every environment resolves the same calculator, and the first transaction always rents one before the
            // reward pass is considered.
            Volatile.Write(ref _rewardCalculator, scope.Component.RewardCalculator);
            if (scope.Component.Executor?.CanSeed != true) return false;
            IBlockTracer<TTrace> tracer = forTransaction(scope.Component.WorldState, hash);
            try
            {
                IBlockTracer cancelable = GethLikeBlockDeadlineTracer.WithCancellation(tracer, token);
                IBlockTracer bounded = TransactionTraceBoundary.WrapAt(cancelable, hash, index, seeds);
                if (bounded is not TransactionTraceBoundary boundary)
                {
                    tracer.TryDispose();
                    return false;
                }

                boundary.IsExecutionRequired = true;

                scope.Component.Processor.Process(OwnCopy(block), TraceProcessingOptions.ReadOnlyReplay, bounded, token);
                if (!boundary.HasExecuted)
                    throw new InvalidOperationException("The indexed trace bypassed transaction prefix execution.");
                traces = tracer.BuildResult();
            }
            catch (Exception exception)
            {
                if (tracer is GethLikeBlockDeadlineTracer deadline) deadline.HandleFailure(exception);
                else tracer.TryDispose();
                throw;
            }
            finally
            {
                if (tracer is GethLikeBlockDeadlineTracer deadline) deadline.Dispose();
            }
        }
        catch
        {
            // Releasing the tracer or the environment failed after the traces were built; nobody else holds them.
            if (traces is not null) ParallelTraceResults<TTrace>.DisposeTraces(traces);
            throw;
        }
        finally
        {
            slots.Release();
        }

        // Outside the permit and the environment on purpose: serializing the traces is the worker's own work, and
        // writing them out ends in a blocking flush of the response, which a client that stops reading can hold
        // indefinitely. A worker that waits there must not be holding one of the node's processing environments while
        // it does, or one unread response stops every trace on the node rather than its own.
        results.Add(index, traces);
        return true;
    }

    private IReadOnlyCollection<TTrace> TraceAfterTransactions<TTrace>(Block block, BlockHeader parent, ParallelTraceBudget slots, IPrefixStateSeedSource seeds,
        Func<IWorldState, IBlockTracer<TTrace>> afterTransactions, CancellationToken token)
    {
        slots.Wait(token);
        try
        {
            using Scope<Components> scope = RentEnvironment(parent);
            IBlockTracer<TTrace> tracer = afterTransactions(scope.Component.WorldState);
            try
            {
                TransactionTraceBoundary boundary = TransactionTraceBoundary.AfterTransactions(tracer.WithCancellation(token), seeds);
                scope.Component.Processor.Process(OwnCopy(block), TraceProcessingOptions.ReadOnlyReplay, boundary, token);
                if (!boundary.HasExecuted)
                    throw new InvalidOperationException("The indexed reward trace bypassed transaction prefix execution.");
                return tracer.BuildResult();
            }
            catch
            {
                tracer.TryDispose();
                throw;
            }
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>Processing stamps the header it is handed, so every run gets a header of its own; the body, and the
    /// senders recovered on it, are shared.</summary>
    private static Block OwnCopy(Block block) => block.WithReplacedHeader(block.Header.Clone());

    private static bool HashesKnown(Transaction[] transactions)
    {
        foreach (Transaction tx in transactions)
        {
            if (tx.Hash is null) return false;
        }

        return true;
    }

    /// <summary>Rethrows the failure of a run once every worker has returned: the caller's own unless it was only
    /// cancelled, then a helper's, which is a real failure when one helper had one.</summary>
    internal static void ThrowFailure(ExceptionDispatchInfo? primary, ExceptionDispatchInfo? helper)
    {
        if (primary is { SourceException: not OperationCanceledException }) primary.Throw();
        helper?.Throw();
        primary?.Throw();
    }

    /// <summary>Waits for active requests, then releases owned workers and environments; the shared budget remains owned by its caller.</summary>
    public void Dispose()
    {
        lock (_runs)
        {
            _disposed = true;
            while (_inFlight > 0) Monitor.Wait(_runs);
        }

        _workers.Dispose();
        _environments.Dispose();
    }

    /// <summary>Processing services owned by one leased environment; never share its world state between active workers.</summary>
    public record Components(IWorldState WorldState, BlockchainProcessorFacade Processor, ISpecProvider SpecProvider, TransactionTraceExecutor? Executor = null, IRewardCalculator? RewardCalculator = null);

    /// <summary>An environment together with the lifetime scope it was resolved from, so the pool that retires it
    /// retires the scope too.</summary>
    public sealed class OwnedEnvironment(IOverridableEnv<Components> inner, ILifetimeScope scope) : IOverridableEnv<Components>, IDisposable
    {
        /// <inheritdoc />
        public bool TryBuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride, IReleaseSpec? specOverride, BlockOverride? blockOverride, [NotNullWhen(true)] out Scope<Components>? scope) =>
            inner.TryBuildAndOverride(header, stateOverride, specOverride, blockOverride, out scope);

        /// <inheritdoc />
        public bool TryBuildAndOverrideAtTarget(BlockHeader targetBlock, Dictionary<Address, AccountOverride>? stateOverride, IReleaseSpec? specOverride, [NotNullWhen(true)] out Scope<Components>? scope) =>
            inner.TryBuildAndOverrideAtTarget(targetBlock, stateOverride, specOverride, out scope);

        /// <summary>Releases the scope owning the processing services.</summary>
        public void Dispose() => scope.Dispose();
    }
}
