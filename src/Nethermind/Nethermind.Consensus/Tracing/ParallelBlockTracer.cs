// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
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
/// the block's senders once.</summary>
public sealed class ParallelBlockTracer : IParallelBlockTracer, IDisposable
{
    public const int MaxDefaultDegree = 16;

    private readonly ShareableOverridableEnvSource<Components> _environments;
    private readonly IPrefixStateSeedSource _seeds;
    private readonly SemaphoreSlim _slots;
    private readonly int _degree;
    private readonly ILogger _logger;

    public ParallelBlockTracer(Func<IOverridableEnv<Components>> buildEnvironment, IPrefixStateSeedSource seeds, int degree, ILogManager logManager)
    {
        _degree = Math.Max(1, degree);
        _environments = new ShareableOverridableEnvSource<Components>(buildEnvironment, _degree);
        _slots = new SemaphoreSlim(_degree, _degree);
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
        [NotNullWhen(true)] out IReadOnlyList<TTrace>? traces)
    {
        traces = null;
        if (_degree < 2 || !_seeds.Enabled || block.Transactions.Length < 2 || !_seeds.TryOpenBlock(block, out ICoveredBlock? covered)) return false;

        using (covered)
        {
            Transaction[] transactions = block.Transactions;
            IReadOnlyCollection<TTrace>?[] results = new IReadOnlyCollection<TTrace>?[transactions.Length + 1];
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Cursor cursor = new();

            TraceOne(block, parent, transactions, cursor.Next(), covered.CreateWorkerSeeds(), forTransaction, results, stop.Token);

            int workers = Math.Min(_degree, transactions.Length - 1);
            Task[] tasks = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                IPrefixStateSeedSource seeds = covered.CreateWorkerSeeds();
                tasks[w] = Task.Factory.StartNew(
                    () => TraceMany(block, parent, transactions, cursor, seeds, forTransaction, results, stop),
                    stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            Await(tasks, token);

            if (afterTransactions is not null)
            {
                results[transactions.Length] = TraceAfterTransactions(block, parent, covered.CreateWorkerSeeds(), afterTransactions, token);
            }

            covered.Complete();

            List<TTrace> all = new(transactions.Length + 1);
            foreach (IReadOnlyCollection<TTrace>? result in results)
            {
                if (result is not null) all.AddRange(result);
            }

            traces = all;
            if (_logger.IsTrace) _logger.Trace($"Traced block {block.Number} in parallel: {transactions.Length} transactions on {workers + 1} workers.");
            return true;
        }
    }

    private void TraceMany<TTrace>(Block block, BlockHeader parent, Transaction[] transactions, Cursor cursor, IPrefixStateSeedSource seeds,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction, IReadOnlyCollection<TTrace>?[] results, CancellationTokenSource stop)
    {
        try
        {
            for (int i = cursor.Next(); i < transactions.Length; i = cursor.Next())
            {
                TraceOne(block, parent, transactions, i, seeds, forTransaction, results, stop.Token);
            }
        }
        catch
        {
            stop.Cancel();
            throw;
        }
    }

    private void TraceOne<TTrace>(Block block, BlockHeader parent, Transaction[] transactions, int index, IPrefixStateSeedSource seeds,
        Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction, IReadOnlyCollection<TTrace>?[] results, CancellationToken token)
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
                if (bounded is not TransactionTraceBoundary) throw new InvalidOperationException("A tracer that traces rewards cannot trace one transaction alone.");

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

    private static void Await(Task[] tasks, CancellationToken token)
    {
        try
        {
            Task.WaitAll(tasks, token);
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

    public void Dispose()
    {
        _environments.Dispose();
        _slots.Dispose();
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
