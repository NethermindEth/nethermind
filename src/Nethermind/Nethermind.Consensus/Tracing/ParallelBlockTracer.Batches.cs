// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Tracing;

public sealed partial class ParallelBlockTracer
{
    internal bool CanBatch(int transactionCount) => GetBatchSize(transactionCount, _degree) > 1;

    private static int GetBatchSize(int transactionCount, int degree) => Math.Clamp((transactionCount - 1) / (2 * degree), 1, 16);

    /// <summary>Traces consecutive transaction batches with one block tracer and processing scope per batch.</summary>
    /// <remarks>The factory traces every transaction it receives and does not trace rewards. Each batch uses normal
    /// sequential block callbacks and state changes; the existing per-transaction entry point retains its isolated
    /// transaction callbacks. A disposable result collection owns the traces it contains.</remarks>
    internal bool TryTraceBatches<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, IBlockTracer<TTrace>> forTransactions,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        CancellationToken token,
        [NotNullWhen(true)] out IReadOnlyList<TTrace>? traces) =>
        Run<TTrace, BatchTraceFactory<TTrace>>(block, parent, new(forTransactions), afterTransactions, emit: null, token, out traces);

    /// <summary>Traces consecutive transaction batches and transfers each completed batch to the emitter in block order.</summary>
    /// <remarks>The emitter owns each collection passed to it, including when it fails. A disposable collection owns
    /// its traces. The publishing worker releases its processing scope and permit before invoking the emitter.</remarks>
    internal bool TryStreamBatches<TTrace>(
        Block block,
        BlockHeader parent,
        Func<IWorldState, IBlockTracer<TTrace>> forTransactions,
        Func<IWorldState, IBlockTracer<TTrace>>? afterTransactions,
        Action<IReadOnlyCollection<TTrace>> emit,
        CancellationToken token) =>
        Run<TTrace, BatchTraceFactory<TTrace>>(block, parent, new(forTransactions), afterTransactions, emit, token, out _);

    private interface ITraceFactory<TTrace>
    {
        int BatchSize(int transactionCount, int degree);
        IBlockTracer<TTrace> Create(IWorldState state, Hash256 firstHash);
        IBlockTracer Bound(IBlockTracer tracer, Transaction[] transactions, int first, int last, IPrefixStateSeedSource seeds, CancellationToken token);
        void DisposeFailed(IBlockTracer<TTrace> tracer, ILogger logger);
        void DisposeResult(IReadOnlyCollection<TTrace> result, ILogger logger);
    }

    private readonly struct TransactionTraceFactory<TTrace>(Func<IWorldState, Hash256, IBlockTracer<TTrace>> create) : ITraceFactory<TTrace>
    {
        public int BatchSize(int transactionCount, int degree) => 1;

        public IBlockTracer<TTrace> Create(IWorldState state, Hash256 firstHash) => create(state, firstHash);

        public IBlockTracer Bound(IBlockTracer tracer, Transaction[] transactions, int first, int last, IPrefixStateSeedSource seeds, CancellationToken token) =>
            TransactionTraceBoundary.Wrap(tracer, transactions[first].Hash, seeds);

        public void DisposeFailed(IBlockTracer<TTrace> tracer, ILogger logger) => tracer.TryDispose();

        public void DisposeResult(IReadOnlyCollection<TTrace> result, ILogger logger)
        {
            foreach (TTrace trace in result) trace.TryDispose();
            result.TryDispose();
        }
    }

    private readonly struct BatchTraceFactory<TTrace>(Func<IWorldState, IBlockTracer<TTrace>> create) : ITraceFactory<TTrace>
    {
        public int BatchSize(int transactionCount, int degree) => GetBatchSize(transactionCount, degree);

        public IBlockTracer<TTrace> Create(IWorldState state, Hash256 firstHash) => create(state);

        public IBlockTracer Bound(IBlockTracer tracer, Transaction[] transactions, int first, int last, IPrefixStateSeedSource seeds, CancellationToken token) =>
            TransactionTraceBoundary.BetweenTransactions(tracer, transactions, first, last, seeds, token);

        public void DisposeFailed(IBlockTracer<TTrace> tracer, ILogger logger)
        {
            try
            {
                try
                {
                    DisposeOwnedResult(tracer.BuildResult());
                }
                finally
                {
                    tracer.TryDispose();
                }
            }
            catch (Exception exception)
            {
                logger.Error("Failed to release an incomplete block trace batch.", exception);
            }
        }

        public void DisposeResult(IReadOnlyCollection<TTrace> result, ILogger logger)
        {
            try
            {
                DisposeOwnedResult(result);
            }
            catch (Exception exception)
            {
                logger.Error("Failed to release an unpublished block trace batch.", exception);
            }
        }

        private static void DisposeOwnedResult(IReadOnlyCollection<TTrace> result)
        {
            if (result is IDisposable owner)
                owner.Dispose();
            else
                foreach (TTrace trace in result) trace.TryDispose();
        }
    }
}
