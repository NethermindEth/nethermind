// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Ingests an ascending PBT leaf stream into an empty target: stages its logical state, then folds its tree.</summary>
/// <remarks>The stream is passed as a factory because staging and folding each take a pass over it.</remarks>
internal static class PbtLeafIngestion
{
    // Below DbOnTheRocks.RocksDbWriteBatch.MaxWritesOnNoWal, so a no-WAL batch is written by its flusher, not inline by the reader.
    internal const int BatchSize = 255;
    private const string StagePhase = "PBT import stage";
    private const string FoldPhase = "PBT import fold";

    /// <summary>Stages the logical accounts, slots and code of the leaves.</summary>
    /// <param name="fraction">The fraction of the pass done after the given number of leaves.</param>
    /// <param name="concurrency">Staging write flushers; zero uses the processor count.</param>
    /// <returns>The staged accounts and slots.</returns>
    public static (ulong Accounts, ulong Slots) Stage(PbtRocksDbPersistence target, Func<CancellationToken, IEnumerable<RebuildEntry>> leaves,
        Func<ulong, float> fraction, int concurrency, ILogManager logManager, CancellationToken cancellationToken)
    {
        (ulong Accounts, ulong Slots, long CodeChunks) staged;
        using (LogicalBatch batch = new(target, concurrency > 0 ? concurrency : Environment.ProcessorCount, cancellationToken))
        {
            staged = PbtLeafStaging.Stage(batch, Reported(StagePhase, leaves(cancellationToken), fraction, logManager), cancellationToken);
            batch.Commit();
        }
        PbtLeafStaging.RebuildCodes(target, staged.CodeChunks, logManager, cancellationToken);
        return (staged.Accounts, staged.Slots);
    }

    /// <summary>Folds the leaves into the tree and publishes them as <paramref name="targetState"/>, unless the root differs from <paramref name="expectedRoot"/>.</summary>
    /// <param name="windowSize">Maximum leaves per fold window; zero uses the rebuilder's default.</param>
    public static async Task<ValueHash256> Fold(PbtRebuilder rebuilder, Func<CancellationToken, IEnumerable<RebuildEntry>> leaves, Func<ulong, float> fraction,
        StateId targetState, int windowSize, ValueHash256? expectedRoot, ILogManager logManager, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(2);
        Task producer = Task.Run(async () =>
        {
            try
            {
                using (PbtRebuilder.EntrySink sink = new(channel.Writer, 2048, linked.Token))
                {
                    foreach (RebuildEntry entry in Reported(FoldPhase, leaves(linked.Token), fraction, logManager)) await sink.Add(entry);
                    await sink.Complete();
                }
                channel.Writer.TryComplete();
            }
            catch (Exception exception) { channel.Writer.TryComplete(exception); throw; }
        }, CancellationToken.None);
        try
        {
            ValueHash256 root = await rebuilder.Rebuild(channel.Reader, targetState, linked.Token, windowSize, expectedRoot);
            await producer;
            return root;
        }
        finally
        {
            await linked.CancelAsync();
            try { await producer; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { while (channel.Reader.TryRead(out ArrayPoolList<RebuildEntry>? chunk)) chunk.Dispose(); }
        }
    }

    /// <summary>The ascending leaves of a spool of tree keys and leaf values.</summary>
    public static IEnumerable<RebuildEntry> SpoolLeaves(PbtSortedSpool spool)
    {
        using PbtSortedSpool.Cursor cursor = spool.Read();
        while (cursor.MoveNext()) yield return new RebuildEntry(new PbtStorageTreeKey(cursor.Key), new ValueHash256(cursor.Value));
    }

    /// <param name="fraction">The fraction of the pass done after the given number of leaves, sampled on the reading
    /// thread since a snapshot's offset briefly moves while its reader re-reads the header section.</param>
    private static IEnumerable<RebuildEntry> Reported(string phase, IEnumerable<RebuildEntry> leaves, Func<ulong, float> fraction, ILogManager logManager)
    {
        ulong read = 0;
        float walked = 0;
        using ProgressReporter progress = PbtImageProgress.Start(phase, "leaf", 0, logManager);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(phase, walked, PbtImageProgress.Counted("leaf", p)));
        foreach (RebuildEntry entry in leaves)
        {
            walked = fraction(++read);
            progress.Update(read);
            yield return entry;
        }
    }

    /// <summary>Compiles staged writes into no-WAL batches on the caller's thread and writes them on parallel flushers.</summary>
    /// <remarks>Staged keys are unique, so the order the flushers write in does not matter. Nothing is durable until
    /// <see cref="Commit"/> flushes the write buffers; the caller wipes an interrupted staging before retrying.</remarks>
    internal sealed class LogicalBatch : IDisposable
    {
        private readonly PbtRocksDbPersistence _target;
        private readonly CancellationTokenSource _cancellation;
        private readonly Task[] _flushers;
        private readonly Channel<IPbtPersistence.IWriteBatch> _pending;
        private ExceptionDispatchInfo? _failure;
        private IPbtPersistence.IWriteBatch? _batch;
        private int _count;

        public LogicalBatch(PbtRocksDbPersistence target, int flusherCount, CancellationToken cancellationToken)
        {
            _target = target;
            _flushers = new Task[flusherCount];
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pending = Channel.CreateBounded<IPbtPersistence.IWriteBatch>(
                new BoundedChannelOptions(2 * flusherCount) { SingleWriter = true });
            for (int i = 0; i < _flushers.Length; i++)
                _flushers[i] = Task.Run(Flush, CancellationToken.None);
        }

        public IPbtPersistence.IWriteBatch Next()
        {
            if (_count == BatchSize) Enqueue();
            _count++;
            return _batch ??= _target.CreateStagingWriteBatch(WriteFlags.DisableWAL);
        }

        /// <summary>Waits for every staged write, then flushes the write buffers so the no-WAL writes are durable.</summary>
        public void Commit()
        {
            if (_batch is not null) Enqueue();
            _pending.Writer.Complete();
            WaitFlushers();
            _target.Flush();
        }

        public void Dispose()
        {
            _pending.Writer.TryComplete();
            _cancellation.Cancel();
            Task.WaitAll(_flushers);
            _batch?.Dispose();
            while (_pending.Reader.TryRead(out IPbtPersistence.IWriteBatch? batch)) batch.Dispose();
            _cancellation.Dispose();
        }

        private void Enqueue()
        {
            try
            {
                if (!_pending.Writer.TryWrite(_batch!)) _pending.Writer.WriteAsync(_batch!, _cancellation.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                WaitFlushers();
                throw;
            }
            _batch = null;
            _count = 0;
        }

        private async Task Flush()
        {
            try
            {
                await foreach (IPbtPersistence.IWriteBatch batch in _pending.Reader.ReadAllAsync(_cancellation.Token))
                    using (batch) batch.Commit();
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(exception), null);
                _cancellation.Cancel();
            }
        }

        /// <summary>Waits for the flushers to stop and rethrows the first failure among them.</summary>
        private void WaitFlushers()
        {
            Task.WaitAll(_flushers);
            _failure?.Throw();
        }
    }
}
