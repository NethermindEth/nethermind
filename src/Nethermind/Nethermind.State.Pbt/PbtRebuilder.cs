// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Rebuilds a canonical EIP-8297 tree in bounded staging windows, then publishes its state.</summary>
public sealed class PbtRebuilder(PbtRocksDbPersistence target, IPbtConfig config, ILogManager logManager)
{
    private const int DefaultWindowSize = 2_000_000;
    /// <summary>The key nibble the zone fold expects each partition batch to be sharded on.</summary>
    private const int PartitionShardNibbleIndex = 2;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();
    private readonly ConcurrencyController _foldQuota = new(config.FoldConcurrency > 0 ? config.FoldConcurrency : Environment.ProcessorCount);
    private readonly FoldFanOut _foldFanOut = new(config.FoldMinOperationsPerWorker, config.FoldLargeSubtreeBytes, config.FoldLargeSubtreeMinOperationsPerWorker);
    private readonly PbtPrefixlessBranchOmission _prefixlessBranchOmission = config.PrefixlessBranchOmission;

    /// <summary>Folds leaf records into staged tree groups and publishes the completed root.</summary>
    /// <param name="source">Owned leaf chunks.</param>
    /// <param name="targetState">The source state identity to publish on success.</param>
    /// <param name="cancellationToken">Cancels consumption and tree updates before publication.</param>
    /// <param name="windowSize">Maximum received records per update; zero uses 2,000,000 records.</param>
    /// <returns>The completed canonical tree root.</returns>
    public Task<ValueHash256> Rebuild(
        ChannelReader<ArrayPoolList<RebuildEntry>> source,
        StateId targetState,
        CancellationToken cancellationToken,
        int windowSize = 0) => Rebuild(source, targetState, cancellationToken, windowSize, WriteFlags.DisableWAL);

    internal async Task<ValueHash256> Rebuild(
        ChannelReader<ArrayPoolList<RebuildEntry>> source,
        StateId targetState,
        CancellationToken cancellationToken,
        int windowSize,
        WriteFlags stagingWriteFlags)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowSize);
        if (windowSize == 0) windowSize = DefaultWindowSize;

        using PbtWriteBatchBuilder<PbtPath> accountChanges = new(PartitionShardNibbleIndex);
        using PbtWriteBatchBuilder<PbtPath> codeChanges = new(PartitionShardNibbleIndex);
        using PbtWriteBatchBuilder<PbtStoragePath> storageChanges = new(PartitionShardNibbleIndex);
        ValueHash256 root = default;
        int windowCount = 0;
        long receivedCount = 0;
        long committedWindows = 0;
        Stopwatch progress = Stopwatch.StartNew();

        await foreach (ArrayPoolList<RebuildEntry> chunk in source.ReadAllAsync(cancellationToken))
        {
            using (chunk)
            {
                foreach (RebuildEntry entry in chunk.AsSpan())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int partition = PbtWriteBatchSet<PbtStorageTreeKey>.PartitionOf(entry.Key);
                    if (partition == (int)PbtPartition.Storage) storageChanges.Set((PbtStoragePath)entry.Key, entry.Leaf);
                    else if (partition == (int)PbtPartition.Code) codeChanges.Set((PbtPath)entry.Key, entry.Leaf);
                    else if (partition == (int)PbtPartition.Account) accountChanges.Set((PbtPath)entry.Key, entry.Leaf);
                    else throw new InvalidDataException($"A canonical account, code or storage key is required: {entry.Key}.");
                    receivedCount++;
                    if (++windowCount == windowSize) CommitWindow();
                }
            }
        }

        if (windowCount != 0) CommitWindow();
        cancellationToken.ThrowIfCancellationRequested();
        target.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        using IPbtPersistence.IWriteBatch batch = target.CreateWriteBatch(StateId.PreGenesis, targetState, root, WriteFlags.None);
        batch.Commit();
        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at {targetState}: {receivedCount} received leaves in {committedWindows} windows, tree root {root}");
        return root;

        void CommitWindow()
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (IPbtPersistence.IReader reader = target.CreateReader())
            using (IPbtPersistence.IWriteBatch stagingBatch = target.CreateStagingWriteBatch(stagingWriteFlags))
            using (PbtPartitionBatches prepared = new())
            {
                if (accountChanges.Count != 0) prepared.Account = accountChanges.Build();
                if (codeChanges.Count != 0) prepared.Code = codeChanges.Build();
                if (storageChanges.Count != 0) prepared.Storage = storageChanges.Build();
                root = TrieUpdater.UpdateRoot(new WindowStore(reader, stagingBatch, cancellationToken), root, prepared, _foldQuota, _foldFanOut, _prefixlessBranchOmission, null);
                cancellationToken.ThrowIfCancellationRequested();
                stagingBatch.Commit();
            }
            accountChanges.Reset();
            codeChanges.Reset();
            storageChanges.Reset();
            windowCount = 0;
            committedWindows++;
            if (progress.Elapsed >= TimeSpan.FromSeconds(10))
            {
                if (_logger.IsInfo) _logger.Info($"PBT rebuild: {receivedCount} received leaves folded in {committedWindows} windows");
                progress.Restart();
            }
        }
    }

    /// <remarks>Zones fold concurrently over a snapshot reader, but the staging write batch is not thread-safe.</remarks>
    private sealed class WindowStore(
        IPbtPersistence.IReader reader,
        IPbtPersistence.IWriteBatch batch,
        CancellationToken cancellationToken) : IPbtStore
    {
        private readonly Lock _writeLock = new();

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return reader.GetNodeGroup(groupKey.ToPath<PbtStorageNodePath>());
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_writeLock) batch.SetNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), payload);
        }
    }
}
