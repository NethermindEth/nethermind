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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Tracing;

/// <summary>Runs the transactions of a covered block on as many processing environments as there are workers. Each
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
/// transaction instead of the second waiting for the whole of the first. The calling thread is one of the workers.</summary>
public sealed class ParallelBlockTracer : IParallelBlockTracer, IDisposable
{
    public const int MaxDefaultDegree = 16;

    private readonly ShareableOverridableEnvSource<Components> _environments;
    private readonly IPrefixStateSeedSource _seeds;
    private readonly SemaphoreSlim _slots;
    private readonly Workers _workers;
    private readonly int _degree;
    private readonly ILogger _logger;
    private readonly object _runs = new();
    private bool _disposed;
    private int _inFlight;

    public ParallelBlockTracer(Func<IOverridableEnv<Components>> buildEnvironment, IPrefixStateSeedSource seeds, int degree, ILogManager logManager)
    {
        _degree = Math.Max(1, degree);
        _environments = new ShareableOverridableEnvSource<Components>(buildEnvironment, _degree);
        _slots = new SemaphoreSlim(_degree, _degree);
        _workers = new Workers(2 * _degree);
        _seeds = seeds;
        _logger = logManager.GetClassLogger<ParallelBlockTracer>();
    }

    /// <summary>0 means one worker per core, capped; anything else is taken as given.</summary>
    public static int DegreeFrom(IFlatDbConfig config) =>
        config.HistoryTransactionIndexTraceParallelism <= 0 ? Math.Clamp(Environment.ProcessorCount, 1, MaxDefaultDegree) : config.HistoryTransactionIndexTraceParallelism;

    public bool TryTrace<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        CancellationToken token,
        [NotNullWhen(true)] out IReadOnlyList<TTrace>? traces) =>
        Run(block, parent, forTransaction, afterTransactions, emit: null, token, out traces);

    public bool TryStream<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        Action<IReadOnlyCollection<TTrace>> emit,
        CancellationToken token) =>
        Run(block, parent, forTransaction, afterTransactions, emit, token, out _);

    private bool Run<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        Action<IReadOnlyCollection<TTrace>>? emit,
        CancellationToken token,
        out IReadOnlyList<TTrace>? traces)
    {
        traces = null;
        if (_degree < 2 || !_seeds.Enabled || block.Transactions.Length < 2 || !HashesKnown(block.Transactions)) return false;

        // Counted under the same lock disposal takes, so a tracer being disposed either sees this run and waits for
        // it or refuses it outright: the caller traces a share of the block on its own thread, and nothing it holds
        // may be disposed under it.
        if (!Enter()) return false;

        try
        {
            if (!_seeds.TryOpenBlock(block, out ICoveredBlock? covered)) return false;

            using (covered)
            {
                Transaction[] transactions = block.Transactions;
                IReadOnlyCollection<TTrace>?[] results = new IReadOnlyCollection<TTrace>?[transactions.Length + 1];
                Emitter<TTrace> emitter = new(results, emit);
                using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                Cursor cursor = new();

                IPrefixStateSeedSource callerSeeds = covered.CreateWorkerSeeds();
                if (!TraceOne(block, parent, transactions, cursor.Next(), callerSeeds, forTransaction, results, emitter, stop.Token)) return false;

                int helpers = Math.Min(_degree, transactions.Length - 1) - 1;
                Task[] tasks = new Task[Math.Max(0, helpers)];
                int queued = 0;
                try
                {
                    for (; queued < tasks.Length; queued++)
                    {
                        IPrefixStateSeedSource seeds = covered.CreateWorkerSeeds();
                        tasks[queued] = _workers.Run(() => TraceMany(block, parent, transactions, cursor, seeds, forTransaction, results, emitter, stop));
                    }

                    TraceMany(block, parent, transactions, cursor, callerSeeds, forTransaction, results, emitter, stop);
                }
                finally
                {
                    Await(tasks.AsSpan(0, queued));
                }

                if (afterTransactions is not null)
                {
                    results[transactions.Length] = TraceAfterTransactions(block, parent, covered.CreateWorkerSeeds(), afterTransactions, token);
                    emitter.Publish();
                }

                covered.Complete();

                if (emit is null)
                {
                    List<TTrace> all = new(transactions.Length + 1);
                    foreach (IReadOnlyCollection<TTrace>? result in results)
                    {
                        if (result is not null) all.AddRange(result);
                    }

                    traces = all;
                }
                else
                {
                    traces = [];
                }

                if (_logger.IsTrace) _logger.Trace($"Traced block {block.Number} in parallel: {transactions.Length} transactions on {tasks.Length + 1} workers.");
                return true;
            }
        }
        finally
        {
            Leave();
        }
    }

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

    private void TraceMany<TTrace>(Block block, BlockHeader parent, Transaction[] transactions, Cursor cursor, IPrefixStateSeedSource seeds,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction, IReadOnlyCollection<TTrace>?[] results, Emitter<TTrace> emitter, CancellationTokenSource stop)
    {
        try
        {
            for (int i = cursor.Next(); i < transactions.Length; i = cursor.Next())
            {
                if (!TraceOne(block, parent, transactions, i, seeds, forTransaction, results, emitter, stop.Token))
                    throw new InvalidOperationException("The tracer bounded the first transaction and refused a later one.");
            }
        }
        catch
        {
            stop.Cancel();
            throw;
        }
    }

    /// <summary>False when the tracer cannot be bounded to one transaction, which only a reward-tracing tracer
    /// cannot; the caller then replays the block as before.</summary>
    private bool TraceOne<TTrace>(Block block, BlockHeader parent, Transaction[] transactions, int index, IPrefixStateSeedSource seeds,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction, IReadOnlyCollection<TTrace>?[] results, Emitter<TTrace> emitter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Hash256 hash = transactions[index].Hash!;
        _slots.Wait(token);
        try
        {
            using Scope<Components> scope = _environments.BuildAndOverride(parent);
            IBlockTracer<TTrace> tracer = forTransaction(scope.Component.WorldState, hash);
            try
            {
                IBlockTracer bounded = TransactionTraceBoundary.Wrap(tracer.WithCancellation(token), hash, seeds);
                if (bounded is not TransactionTraceBoundary)
                {
                    tracer.TryDispose();
                    return false;
                }

                scope.Component.Processor.Process(OwnCopy(block), TraceProcessingOptions.ReadOnlyReplay, bounded, token);
                results[index] = tracer.BuildResult();
            }
            catch
            {
                tracer.TryDispose();
                throw;
            }
        }
        finally
        {
            _slots.Release();
        }

        // Outside the permit and the environment on purpose: writing the trace out ends in a blocking flush of the
        // response, which a client that stops reading can hold indefinitely. A worker that waits there must not be
        // holding one of the node's processing environments while it does, or one unread response stops every trace
        // on the node rather than its own.
        emitter.Publish();
        return true;
    }

    private IReadOnlyCollection<TTrace> TraceAfterTransactions<TTrace>(Block block, BlockHeader parent, IPrefixStateSeedSource seeds,
        Func<IWorldState, IBlockTracer<TTrace>> afterTransactions, CancellationToken token)
    {
        _slots.Wait(token);
        try
        {
            using Scope<Components> scope = _environments.BuildAndOverride(parent);
            IBlockTracer<TTrace> tracer = afterTransactions(scope.Component.WorldState);
            try
            {
                scope.Component.Processor.Process(OwnCopy(block), TraceProcessingOptions.ReadOnlyReplay, TransactionTraceBoundary.AfterTransactions(tracer.WithCancellation(token), seeds), token);
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
            _slots.Release();
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
    /// what they hold is released only after they are gone.</summary>
    private static void Await(ReadOnlySpan<Task> tasks)
    {
        if (tasks.IsEmpty) return;

        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException e)
        {
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
    }

    /// <summary>Waits for the runs already under way, however long their requests take, and refuses the ones that
    /// arrive from here on so they are replayed instead. A run is not finished until its workers are, so once the
    /// count reaches zero nothing is holding an environment or a thread.</summary>
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
    /// interleaves their transactions. Disposal drains, so a node shutting down waits for the jobs in flight; they
    /// stop on their request's timeout, and a block whose jobs arrive after that waits for nothing: the caller
    /// traces it alone.</summary>
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

        /// <summary>A completed task once the workers are shutting down: the caller then traces the whole block
        /// itself rather than failing a request that would otherwise have fallen back to the replay.</summary>
        public Task Run(Action job)
        {
            if (_jobs.IsAddingCompleted) return Task.CompletedTask;

            _ = _threads.Value;
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _jobs.Add(() =>
                {
                    try
                    {
                        job();
                        done.SetResult();
                    }
                    catch (Exception e)
                    {
                        done.SetException(e);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // Adding completed between the test and the add; the caller traces the block itself.
                return Task.CompletedTask;
            }

            return done.Task;
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

    /// <summary>Hands finished traces on in block order: a worker that finishes out of turn leaves its result for
    /// the transaction before it, and whoever closes the gap passes the run on. Under the lock, so the sink is
    /// written by one thread at a time, and a trace already handed on is released.</summary>
    private sealed class Emitter<TTrace>(IReadOnlyCollection<TTrace>?[] results, Action<IReadOnlyCollection<TTrace>>? emit)
    {
        private readonly Lock _lock = new();
        private int _next;

        public void Publish()
        {
            if (emit is null) return;

            lock (_lock)
            {
                while (_next < results.Length && results[_next] is { } ready)
                {
                    // Taken before it is written: a write that fails leaves a doomed response either way, and losing
                    // the trace is better than a sibling finding the slot still armed and writing it twice.
                    results[_next] = null;
                    _next++;
                    emit(ready);
                }
            }
        }
    }

    private sealed class Cursor
    {
        private int _next = -1;

        public int Next() => Interlocked.Increment(ref _next);
    }

    public record Components(IWorldState WorldState, BlockchainProcessorFacade Processor);

    /// <summary>An environment together with the lifetime scope it was resolved from, so the pool that retires it
    /// retires the scope too.</summary>
    public sealed class OwnedEnvironment(IOverridableEnv<Components> inner, ILifetimeScope scope) : IOverridableEnv<Components>, IDisposable
    {
        public Scope<Components> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null, IReleaseSpec? specOverride = null, BlockOverride? blockOverride = null) =>
            inner.BuildAndOverride(header, stateOverride, specOverride, blockOverride);

        public void Dispose() => scope.Dispose();
    }
}
