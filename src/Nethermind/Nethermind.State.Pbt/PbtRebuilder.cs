// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Rebuilds a canonical EIP-8297 tree in bounded staging windows, then publishes its state.</summary>
public sealed class PbtRebuilder(PbtRocksDbPersistence target, IPbtConfig config, int foldConcurrency, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();
    private readonly ConcurrencyController _foldQuota = new(foldConcurrency > 0 ? foldConcurrency : Environment.ProcessorCount);
    private readonly FoldFanOut _foldFanOut = new(config.FoldMinOperationsPerWorker, config.FoldLargeSubtreeBytes, config.FoldLargeSubtreeMinOperationsPerWorker);

    public PbtRebuilder(PbtRocksDbPersistence target, IPbtConfig config, ILogManager logManager)
        : this(target, config, config.FoldConcurrency, logManager)
    {
    }

    /// <summary>Folds leaf records into staged tree groups and publishes the completed root.</summary>
    /// <param name="source">Owned leaf chunks.</param>
    /// <param name="targetState">The source state identity to publish on success.</param>
    /// <param name="cancellationToken">Cancels consumption and tree updates before publication.</param>
    /// <param name="windowSize">Maximum received records per update; zero uses 2,000,000 records.</param>
    /// <param name="expectedRoot">When set, a completed root that differs is refused before anything is published.</param>
    /// <param name="publishAfter">Publication waits for it, and is abandoned when it fails.</param>
    /// <returns>The completed canonical tree root.</returns>
    internal async Task<ValueHash256> Rebuild(
        ChannelReader<ArrayPoolList<RebuildEntry>> source,
        StateId targetState,
        CancellationToken cancellationToken,
        int windowSize,
        ValueHash256? expectedRoot,
        Task publishAfter)
    {
        if (windowSize == 0) windowSize = PbtSortedLeafFold.DefaultWindowSize;

        long receivedCount = 0;
        long committedWindows = 0;
        Stopwatch progress = Stopwatch.StartNew();
        ValueHash256 root;
        using (StagingSink staging = new(target, cancellationToken))
        {
            root = await PbtSortedLeafFold.FoldWindows(source, staging, windowSize, _foldQuota, _foldFanOut, CommitWindow, cancellationToken);

            void CommitWindow(int leaves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                staging.Commit();
                receivedCount += leaves;
                committedWindows++;
                if (progress.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    if (_logger.IsInfo) _logger.Info($"PBT rebuild: {receivedCount} received leaves folded in {committedWindows} windows");
                    progress.Restart();
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        target.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedRoot is { } expected && root != expected)
            throw new InvalidDataException("Staged PBT root differs from the snapshot's claimed root.");
        await publishAfter;
        cancellationToken.ThrowIfCancellationRequested();
        using IPbtPersistence.IWriteBatch batch = target.CreateWriteBatch(StateId.PreGenesis, targetState, root, WriteFlags.None);
        batch.Commit();
        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at {targetState}: {receivedCount} received leaves in {committedWindows} windows, tree root {root}");
        return root;
    }

    /// <summary>Writes a window's groups into one staging batch, committed once the window folds.</summary>
    /// <remarks>Not thread-safe: <see cref="PbtRightmostGroupStore"/> serializes its writes.</remarks>
    private sealed class StagingSink(PbtRocksDbPersistence target, CancellationToken cancellationToken) : IPbtNodeGroupSink, IDisposable
    {
        private IPbtPersistence.IWriteBatch? _batch;

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (_batch ??= target.CreateStagingWriteBatch(WriteFlags.DisableWAL)).SetNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), payload);
        }

        public void Commit()
        {
            if (_batch is null) return;
            _batch.Commit();
            _batch.Dispose();
            _batch = null;
        }

        public void Dispose() => _batch?.Dispose();
    }
}
