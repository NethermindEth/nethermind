// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Blockchain;

namespace Nethermind.Consensus.Processing;

public partial class BlockCachePreWarmer
{
    /// <summary>
    /// Tracks which speculative runs a committed transaction invalidates, so they can be warmed again against the
    /// block's committed values before the main thread reaches them.
    /// </summary>
    /// <remarks>
    /// Speculation on parent state diverges from canonical execution exactly where an earlier transaction of the
    /// same block changed what a later one reads. An inverted index from each key to the transactions whose
    /// speculative run read it makes a commit cost one lookup per written key. Only transactions at least
    /// <see cref="MinLead"/> ahead of the committing one are re-warmed: the main thread starts the next one
    /// immediately, so a re-warm of it could not get ahead.
    /// </remarks>
    internal sealed class LookaheadRewarmer(int transactionCount)
    {
        private const int MinLead = 2;
        private const int Fresh = 0;
        private const int Pending = 1;
        private const int Running = 2;
        private const int Dirty = 3;

        private readonly ConcurrentDictionary<AddressAsKey, List<int>> _accountReaders = new();
        private readonly ConcurrentDictionary<StorageCell, List<int>> _slotReaders = new();
        private readonly int[] _state = new int[transactionCount];
        private readonly ConcurrentQueue<int> _pending = new();
        private int _rewarmed;

        public int Rewarmed => Volatile.Read(ref _rewarmed);

        /// <summary>Records what a speculative run of <paramref name="txIndex"/> read.</summary>
        public void Publish(int txIndex, PreBlockCaches.ReadSet reads)
        {
            foreach (AddressAsKey address in reads.Accounts) AddReader(_accountReaders, address, txIndex);
            foreach (StorageCell cell in reads.Slots) AddReader(_slotReaders, cell, txIndex);
        }

        /// <summary>Marks the speculative runs a committed write set invalidates.</summary>
        public void OnCommitted(PreBlockCaches.CommittedWriteSet writes)
        {
            int minIndex = writes.TxIndex + MinLead;
            foreach (AddressAsKey address in writes.Accounts) MarkReaders(_accountReaders, address, minIndex);
            foreach (StorageCell cell in writes.Slots) MarkReaders(_slotReaders, cell, minIndex);
        }

        /// <summary>Moves the pending transactions the main thread has not reached into <paramref name="batch"/>.</summary>
        public void TakePending(ArrayPoolList<int> batch, int mainThreadTxIndex)
        {
            while (_pending.TryDequeue(out int txIndex))
            {
                if (txIndex <= mainThreadTxIndex + 1)
                {
                    Volatile.Write(ref _state[txIndex], Fresh);
                    continue;
                }

                Volatile.Write(ref _state[txIndex], Running);
                batch.Add(txIndex);
            }
        }

        /// <summary>Ends a re-warm; a commit that landed meanwhile queues the transaction again.</summary>
        public void Completed(int txIndex)
        {
            Interlocked.Increment(ref _rewarmed);
            if (Interlocked.Exchange(ref _state[txIndex], Fresh) == Dirty) MarkStale(txIndex);
        }

        private static void AddReader<TKey>(ConcurrentDictionary<TKey, List<int>> index, TKey key, int txIndex) where TKey : notnull
        {
            List<int> readers = index.GetOrAdd(key, static _ => new List<int>(2));
            lock (readers)
            {
                if (!readers.Contains(txIndex)) readers.Add(txIndex);
            }
        }

        private void MarkReaders<TKey>(ConcurrentDictionary<TKey, List<int>> index, TKey key, int minIndex) where TKey : notnull
        {
            if (!index.TryGetValue(key, out List<int>? readers)) return;

            lock (readers)
            {
                foreach (int reader in readers)
                {
                    if (reader >= minIndex) MarkStale(reader);
                }
            }
        }

        private void MarkStale(int txIndex)
        {
            if (Interlocked.CompareExchange(ref _state[txIndex], Pending, Fresh) == Fresh)
            {
                _pending.Enqueue(txIndex);
            }
            else
            {
                Interlocked.CompareExchange(ref _state[txIndex], Dirty, Running);
            }
        }
    }

    /// <summary>Commit silence after which the re-warm loop assumes the block is not being executed and stops.</summary>
    private static readonly TimeSpan RewarmIdleTimeout = TimeSpan.FromSeconds(1);

    private void RewarmOnCommits(BlockState blockState, ParallelOptions parallelOptions)
    {
        LookaheadRewarmer lookahead = blockState.Lookahead!;
        CancellationToken token = parallelOptions.CancellationToken;
        int lastIndex = blockState.Block.Transactions.Length - 1;
        using ArrayPoolList<int> batch = new(16);
        SpinWait spinner = default;
        long lastProgress = Stopwatch.GetTimestamp();
        // The consumer scope closing means the block is done; a long silence means no main thread is executing it.
        while (!token.IsCancellationRequested && _preBlockCaches!.ConsumerScopeOpen && MainThreadTxIndex < lastIndex)
        {
            bool progressed = false;
            while (_preBlockCaches!.TryDequeueCommitted(out PreBlockCaches.CommittedWriteSet? writes))
            {
                using (writes) lookahead.OnCommitted(writes);
                progressed = true;
            }

            batch.Clear();
            lookahead.TakePending(batch, MainThreadTxIndex);
            if (batch.Count > 0)
            {
                RewarmBatch(blockState, parallelOptions, batch);
                progressed = true;
            }

            if (progressed)
            {
                lastProgress = Stopwatch.GetTimestamp();
            }
            else
            {
                if (Stopwatch.GetElapsedTime(lastProgress) > RewarmIdleTimeout) break;
                spinner.SpinOnce(sleep1Threshold: -1);
            }
        }

        if (_logger.IsDebug) _logger.Debug($"Lookahead re-warmed {lookahead.Rewarmed} transactions of block {blockState.Block.Number}");
    }

    private static void RewarmBatch(BlockState blockState, ParallelOptions parallelOptions, ArrayPoolList<int> batch) =>
        ParallelUnbalancedWork.For(
            0,
            batch.Count,
            parallelOptions,
            () => new RewarmWorker(blockState, batch, parallelOptions.CancellationToken),
            static (i, worker) =>
            {
                BlockState blockState = worker.BlockState;
                int txIndex = worker.Batch[i];
                try
                {
                    using IReadOnlyTxProcessingScope scope = worker.Env.Build(blockState.Parent);
                    scope.TransactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(blockState.Block.Header, blockState.Spec));
                    WarmupSingleTransaction(scope, blockState.Block.Transactions[txIndex], txIndex, blockState, worker.Token);
                }
                finally
                {
                    blockState.Lookahead!.Completed(txIndex);
                }

                return worker;
            },
            static worker => worker.ReturnEnv());

    private sealed class RewarmWorker(BlockState blockState, ArrayPoolList<int> batch, CancellationToken token)
    {
        public readonly BlockState BlockState = blockState;
        public readonly ArrayPoolList<int> Batch = batch;
        public readonly CancellationToken Token = token;
        public readonly IReadOnlyTxProcessorSource Env = blockState.PreWarmer._envPool.Get();

        public void ReturnEnv() => BlockState.PreWarmer._envPool.Return(Env);
    }
}
