// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>Node-group store for folding strictly ascending leaves in windows; it retains only the rightmost group at each depth.</summary>
/// <remarks>
/// A key above every folded key reaches an existing group only when that group's path prefixes the largest folded key,
/// so the rightmost group at each depth is the only one a later window can read, and every other write is dropped.
/// The fold runs serially in ascending leaf order, so no write replaces a group still to be read.
/// </remarks>
internal sealed class PbtRightmostGroupStore : IPbtStore, IPbtNodeGroupSink, IDisposable
{
    internal const int DefaultWindowSize = 2_000_000;
    /// <summary>The key nibble the zone fold expects each partition batch to be sharded on.</summary>
    private const int PartitionShardNibbleIndex = 2;

    private readonly Group[] _edge = new Group[PbtStorageTreeKey.MaxLength * 8 + 1];

    /// <summary>Calculates an EIP-8297 root from strictly ordered image leaves.</summary>
    /// <param name="windowSize">Maximum leaves folded per tree update.</param>
    internal static ValueHash256 CalculateRoot(IEnumerable<RebuildEntry> entries, int windowSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        using PbtRightmostGroupStore store = new();
        using PbtWriteBatchBuilder<PbtPath> accountChanges = new(PartitionShardNibbleIndex);
        using PbtWriteBatchBuilder<PbtPath> codeChanges = new(PartitionShardNibbleIndex);
        using PbtWriteBatchBuilder<PbtStoragePath> storageChanges = new(PartitionShardNibbleIndex);
        ConcurrencyController foldQuota = new(1);
        FoldFanOut fanOut = new(FoldFanOut.DefaultMinOperationsPerWorker, FoldFanOut.DefaultLargeSubtreeBytes, FoldFanOut.DefaultLargeSubtreeMinOperationsPerWorker);
        ValueHash256 root = default;
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
            if (++windowCount == windowSize) FoldWindow();
        }

        if (windowCount != 0) FoldWindow();
        return root;

        void FoldWindow()
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (PbtPartitionBatches prepared = new())
            {
                if (accountChanges.Count != 0) prepared.Account = accountChanges.Build();
                if (codeChanges.Count != 0) prepared.Code = codeChanges.Build();
                if (storageChanges.Count != 0) prepared.Storage = storageChanges.Build();
                root = TrieUpdater.UpdateRoot(store, root, prepared, foldQuota, fanOut, null);
            }
            accountChanges.Reset();
            codeChanges.Reset();
            storageChanges.Reset();
            windowCount = 0;
        }
    }

    public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash)
    {
        Group group = _edge[groupKey.BitDepth];
        if (group.Payload is null || !group.Path.Equals(groupKey.ToPath<PbtStorageNodePath>())) return null;
        group.Payload.AcquireLease();
        return group.Payload;
    }

    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
    {
        PbtStorageNodePath path = groupKey.ToPath<PbtStorageNodePath>();
        ref Group edge = ref _edge[groupKey.BitDepth];
        // Shared groups above the zones' units are published after the units, in descending path order.
        if (edge.Payload is not null && path.CompareTo(edge.Path) < 0) return;
        payload?.AcquireLease();
        ((IDisposable?)edge.Payload)?.Dispose();
        edge = new(path, payload);
    }

    public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

    public void Dispose()
    {
        foreach (Group group in _edge) ((IDisposable?)group.Payload)?.Dispose();
    }

    private readonly record struct Group(PbtStorageNodePath Path, RefCountingMemory? Payload);
}
