// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>Node-group store for folding strictly ascending leaves in windows; it retains only the rightmost group at each depth.</summary>
/// <remarks>
/// A key above every folded key reaches an existing group only when that group's path prefixes the largest folded key,
/// so the rightmost group at each depth is the only one a later window can read, and every other write is dropped.
/// Zones and buckets fold concurrently, so a group superseded during a fold stays readable until the fold ends.
/// </remarks>
internal sealed class PbtRightmostGroupStore : IPbtStore, IPbtNodeGroupSink, IDisposable
{
    internal const int DefaultWindowSize = 2_000_000;
    /// <summary>The key nibble the zone fold expects each partition batch to be sharded on.</summary>
    private const int PartitionShardNibbleIndex = 2;

    private readonly Lock _lock = new();
    private readonly Group[] _edge = new Group[PbtStorageTreeKey.MaxLength * 8 + 1];
    /// <summary>The group each depth held when the fold started, once superseded; released when the fold ends.</summary>
    private readonly Group[] _superseded = new Group[PbtStorageTreeKey.MaxLength * 8 + 1];

    /// <summary>Calculates an EIP-8297 root from strictly ordered image leaves.</summary>
    /// <remarks>The next window is read and batched on another thread while the current one folds.</remarks>
    /// <param name="windowSize">Maximum leaves folded per tree update.</param>
    internal static ValueHash256 CalculateRoot(IEnumerable<RebuildEntry> entries, int windowSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        using CancellationTokenSource readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using BlockingCollection<PbtPartitionBatches> windows = new(boundedCapacity: 1);
        Task reading = Task.Run(() => ReadWindows(entries, windowSize, windows, readCancellation.Token), readCancellation.Token);
        try
        {
            using PbtRightmostGroupStore store = new();
            ConcurrencyController foldQuota = new(Environment.ProcessorCount);
            FoldFanOut fanOut = new(FoldFanOut.DefaultMinOperationsPerWorker, FoldFanOut.DefaultLargeSubtreeBytes, FoldFanOut.DefaultLargeSubtreeMinOperationsPerWorker);
            ValueHash256 root = default;
            foreach (PbtPartitionBatches window in windows.GetConsumingEnumerable(cancellationToken))
            {
                using (window) root = TrieUpdater.UpdateRoot(store, root, window, foldQuota, fanOut, null);
                store.ReleaseSuperseded();
            }
            reading.GetAwaiter().GetResult();
            return root;
        }
        finally
        {
            readCancellation.Cancel();
            Task.WhenAny(reading).GetAwaiter().GetResult();
            while (windows.TryTake(out PbtPartitionBatches? unfolded)) unfolded.Dispose();
        }
    }

    private static void ReadWindows(IEnumerable<RebuildEntry> entries, int windowSize,
        BlockingCollection<PbtPartitionBatches> windows, CancellationToken cancellationToken)
    {
        try
        {
            using PbtWriteBatchBuilder<PbtPath> accountChanges = new(PartitionShardNibbleIndex);
            using PbtWriteBatchBuilder<PbtPath> codeChanges = new(PartitionShardNibbleIndex);
            using PbtWriteBatchBuilder<PbtStoragePath> storageChanges = new(PartitionShardNibbleIndex);
            PbtStorageTreeKey previous = default;
            int windowCount = 0;
            foreach (RebuildEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous.Length != 0 && previous.CompareTo(entry.Key) >= 0)
                    throw new InvalidDataException("Image leaves must be strictly ordered.");
                previous = entry.Key;
                int partition = PbtPartitions.PartitionOf(entry.Key);
                if (partition == (int)PbtPartition.Storage) storageChanges.Set((PbtStoragePath)entry.Key, entry.Leaf);
                else if (partition == (int)PbtPartition.Code) codeChanges.Set((PbtPath)entry.Key, entry.Leaf);
                else if (partition == (int)PbtPartition.Account) accountChanges.Set((PbtPath)entry.Key, entry.Leaf);
                else throw new InvalidDataException($"A canonical account, code or storage key is required: {entry.Key}.");
                if (++windowCount == windowSize) AddWindow();
            }

            if (windowCount != 0) AddWindow();

            void AddWindow()
            {
                PbtPartitionBatches window = new();
                try
                {
                    if (accountChanges.Count != 0) window.Account = accountChanges.Build();
                    if (codeChanges.Count != 0) window.Code = codeChanges.Build();
                    if (storageChanges.Count != 0) window.Storage = storageChanges.Build();
                    windows.Add(window, cancellationToken);
                }
                catch
                {
                    window.Dispose();
                    throw;
                }
                accountChanges.Reset();
                codeChanges.Reset();
                storageChanges.Reset();
                windowCount = 0;
            }
        }
        finally
        {
            windows.CompleteAdding();
        }
    }

    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
    {
        PbtStorageNodePath path = groupKey.ToPath<PbtStorageNodePath>();
        lock (_lock)
        {
            Group group = _edge[groupKey.BitDepth];
            if (!group.Holds(path)) group = _superseded[groupKey.BitDepth];
            if (!group.Holds(path)) return null;
            group.Payload!.AcquireLease();
            return group.Payload;
        }
    }

    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
    {
        PbtStorageNodePath path = groupKey.ToPath<PbtStorageNodePath>();
        lock (_lock)
        {
            ref Group edge = ref _edge[groupKey.BitDepth];
            if (edge.Payload is not null && path.CompareTo(edge.Path) < 0) return;
            payload?.AcquireLease();
            ref Group superseded = ref _superseded[groupKey.BitDepth];
            if (superseded.Payload is null) superseded = edge;
            else ((IDisposable?)edge.Payload)?.Dispose();
            edge = new(path, payload);
        }
    }

    public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

    private void ReleaseSuperseded()
    {
        foreach (ref Group group in _superseded.AsSpan())
        {
            ((IDisposable?)group.Payload)?.Dispose();
            group = default;
        }
    }

    public void Dispose()
    {
        ReleaseSuperseded();
        foreach (Group group in _edge) ((IDisposable?)group.Payload)?.Dispose();
    }

    private readonly record struct Group(PbtStorageNodePath Path, RefCountingMemory? Payload)
    {
        public bool Holds(in PbtStorageNodePath path) => Payload is not null && Path.Equals(path);
    }
}
