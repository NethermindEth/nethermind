// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class ParallelBlockTracer : IParallelBlockTracer, IDisposable
{
    private readonly ShareableOverridableEnvSource<Components> _environments;
    private readonly IPrefixStateSeedSource _seeds;
    private readonly ParallelTraceBudgets _budgets;
    private readonly Workers _workers;
    private readonly SerializedTraces.Pool _serialized;
    private readonly int _degree;
    private readonly ILogger _logger;
    private readonly object _runs = new();

    /// <summary>Serialized transactions per degree kept for the next blocks; a run in flight may hold more.</summary>
    private const int PooledPerDegree = 8;

    private bool _disposed;
    private int _inFlight;
    private IRewardCalculator? _rewardCalculator;

    /// <summary>Transactions per worker that a streamed block may have traced and not yet written: enough for the
    /// workers to pass a transaction that costs as much as many others, few enough that the traces waiting behind it
    /// stay bounded. The value is not measured; the node A/B decides it.</summary>
    internal int StreamedPerWorker { get; init; } = 32;

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
        _serialized = new SerializedTraces.Pool(_degree * PooledPerDegree);
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
            CollectedTraceResults<TTrace>? collected = null;
            using ParallelTraceResults<TTrace> results = stream is null
                ? collected = new CollectedTraceResults<TTrace>(transactions.Length + 1)
                : CreateEmitter(stream, transactions.Length, workers);
            try
            {
                using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                using CancellationTokenSource unstarted = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                Cursor cursor = new();

                IPrefixStateSeedSource callerSeeds = covered.CreateWorkerSeeds();
                results.Reserve(stop.Token);
                if (!TraceOne(block, parent, transactions, cursor.Next(), slots, callerSeeds, forTransaction, results, stop.Token)) return false;

                Task[] tasks = new Task[workers - 1];
                int queued = 0;
                ExceptionDispatchInfo? failure = null;
                try
                {
                    for (; queued < tasks.Length; queued++)
                    {
                        IPrefixStateSeedSource seeds = covered.CreateWorkerSeeds();
                        tasks[queued] = QueueWorker(() => TraceMany(block, parent, transactions, cursor, slots, seeds, forTransaction, results, stop), unstarted.Token);
                    }

                    TraceMany(block, parent, transactions, cursor, slots, callerSeeds, forTransaction, results, stop);
                    // The cursor is spent, so a helper that has not started would find nothing to trace; with the
                    // worker threads held by other blocks it might not start before those finish.
                    unstarted.Cancel();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                    stop.Cancel();
                }
                finally
                {
                    Await(tasks.AsSpan(0, queued), failure);
                }

                // Only rewards are traced after the transactions, so a block that is never rewarded needs no replay,
                // and no permit or environment either.
                if (afterTransactions is not null && Volatile.Read(ref _rewardCalculator)?.MayReward(block.Header) != false)
                {
                    results.Add(transactions.Length, TraceAfterTransactions(block, parent, slots, covered.CreateWorkerSeeds(), afterTransactions, token));
                }

                results.Complete();
                covered.Complete();
            }
            catch
            {
                // Every worker has returned by now: Await let the failure out only once they had. What they finished
                // and nobody received is this run's to release.
                results.DisposeUnpublished();
                throw;
            }

            traces = collected is null ? [] : collected.Flatten();

            if (_logger.IsTrace) _logger.Trace($"Traced block {block.Number} in parallel: {transactions.Length} transactions on {workers} workers.");
            return true;
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>A streamed block serializes its traces on the workers when the stream allows it, and in block order
    /// otherwise; either way only a window of transactions per worker is traced ahead of the response.</summary>
    private ParallelTraceResults<TTrace> CreateEmitter<TTrace>(IParallelTraceStream<TTrace> stream, int transactions, int workers)
    {
        int window = workers * StreamedPerWorker;
        return stream.SerializeAheadOptions is { } options
            ? new SerializingTraceEmitter<TTrace>(stream, options, _serialized, transactions, window)
            : new InTurnTraceEmitter<TTrace>(stream, transactions, window);
    }

    internal Task QueueWorker(Action action, CancellationToken token) => _workers.Run(action, token);

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

    private void TraceMany<TTrace>(Block block, BlockHeader parent, Transaction[] transactions, Cursor cursor, ParallelTraceBudget slots, IPrefixStateSeedSource seeds,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction, ParallelTraceResults<TTrace> results, CancellationTokenSource stop)
    {
        try
        {
            while (true)
            {
                results.Reserve(stop.Token);
                int i = cursor.Next();
                if (i >= transactions.Length)
                {
                    results.Unreserve();
                    return;
                }

                if (!TraceOne(block, parent, transactions, i, slots, seeds, forTransaction, results, stop.Token))
                    throw new InvalidOperationException("The tracer bounded the first transaction and refused a later one.");
            }
        }
        catch
        {
            stop.Cancel();
            throw;
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
                IBlockTracer cancelable = tracer is GethLikeBlockDeadlineTracer deadline
                    ? GethLikeBlockDeadlineTracer.WithCancellation(deadline, token)
                    : tracer.WithCancellation(token);
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

    /// <summary>Waits for every worker whatever happens: they stop on their own once the request is cancelled, and
    /// what they hold is released only after they are gone. A worker that never started traced nothing, so when no
    /// worker failed and the caller did not either, the run is complete.</summary>
    internal static void Await(ReadOnlySpan<Task> tasks, ExceptionDispatchInfo? primary = null)
    {
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException e)
        {
            if (primary is null && NoneFaulted(tasks)) return;
            if (primary is not null && primary.SourceException is not OperationCanceledException) primary.Throw();
            Exception first = e.InnerExceptions[0];
            foreach (Exception inner in e.InnerExceptions)
            {
                if (inner is not OperationCanceledException)
                {
                    first = inner;
                    break;
                }
            }

            ExceptionDispatchInfo.Capture(first).Throw();
        }
        primary?.Throw();
    }

    private static bool NoneFaulted(ReadOnlySpan<Task> tasks)
    {
        foreach (Task task in tasks)
        {
            if (task.IsFaulted) return false;
        }

        return true;
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

    /// <summary>Threads started when the first block asks and kept for the next one. A job is one worker's share of
    /// one block; with more threads than the degree, the jobs of two blocks run side by side and the semaphore
    /// interleaves their transactions. Every job belongs to a run the tracer has counted in, and disposal waits for
    /// those runs before it stops taking jobs, so no job is ever refused.</summary>
    private sealed class Workers : IDisposable
    {
        private readonly BlockingCollection<Action> _jobs = [];
        private readonly Lazy<Thread[]> _threads;

        public Workers(int count) => _threads = new Lazy<Thread[]>(() =>
        {
            Thread[] threads = new Thread[count];
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(Work) { IsBackground = true, Name = $"{nameof(ParallelBlockTracer)} {i}" };
                threads[i].Start();
            }

            return threads;
        });

        public Task Run(Action job, CancellationToken token)
        {
            _ = _threads.Value;
            QueuedJob queued = new(job, token);
            _jobs.Add(queued.Execute);
            return queued.Completion;
        }

        private sealed class QueuedJob
        {
            private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;
            private Action? _action;
            private int _state;

            public QueuedJob(Action action, CancellationToken token)
            {
                _action = action;
                _registration = token.UnsafeRegister(static state => ((QueuedJob)state!).Cancel(), this);
            }

            public Task Completion => _done.Task;

            private void Cancel()
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
                Interlocked.Exchange(ref _action, null);
                _done.TrySetCanceled();
            }

            public void Execute()
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                {
                    _registration.Dispose();
                    return;
                }
                try
                {
                    Interlocked.Exchange(ref _action, null)!();
                    _done.SetResult();
                }
                catch (Exception e)
                {
                    _done.SetException(e);
                }
                finally
                {
                    _registration.Dispose();
                }
            }
        }

        private void Work()
        {
            foreach (Action job in _jobs.GetConsumingEnumerable())
            {
                job();
            }
        }

        public void Dispose()
        {
            _jobs.CompleteAdding();
            if (_threads.IsValueCreated)
            {
                foreach (Thread thread in _threads.Value) thread.Join();
            }

            _jobs.Dispose();
        }
    }

    private sealed class Cursor
    {
        private int _next = -1;

        public int Next() => Interlocked.Increment(ref _next);
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
