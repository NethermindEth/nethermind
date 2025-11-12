// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Blockchain;

public class BlockhashCache(IHeaderStore headerStore, ILogManager logManager) : IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ConcurrentDictionary<Hash256AsKey, BlockData> _blocks = new();
    private readonly ConcurrentDictionary<int, AncestorSnapshot> _snapshots = new();
    private int _nextSnapshotId;
    private const int SegmentSize = 256;
    private long _minBlock = long.MaxValue;

    public void Set(BlockHeader blockHeader)
    {
        int snapshotId;

        if (blockHeader.ParentHash is null || !_blocks.TryGetValue(blockHeader.ParentHash, out BlockData parent))
        {
            // Genesis or orphan - create fresh snapshot
            snapshotId = CreateSnapshot(blockHeader);
        }
        else
        {
            SnapshotCompatibility compatibility = CheckSnapshotCompatibility(parent.SnapshotId, blockHeader, out AncestorSnapshot snapshot);

            snapshotId = compatibility switch
            {
                SnapshotCompatibility.NewSegment => CreateSnapshot(blockHeader, parent.SnapshotId),
                SnapshotCompatibility.Fork => CreateSnapshotFromParent(blockHeader, snapshot),
                SnapshotCompatibility.Reuse => ReuseSnapshot(blockHeader, snapshot, parent.SnapshotId),
                SnapshotCompatibility.Orphaned => CreateSnapshot(blockHeader),
                _ => throw new ArgumentOutOfRangeException(nameof(compatibility), compatibility, null)
            };
        }

        _minBlock = Math.Min(blockHeader.Number, _minBlock);
        _blocks[blockHeader.Hash!] = new BlockData
        {
            Block = new BlockInfo(blockHeader),
            SnapshotId = snapshotId
        };
    }

    private int ReuseSnapshot(BlockHeader blockHeader, AncestorSnapshot snapshot, int snapshotId)
    {
        if (!snapshot.IsDisposed)
        {
            lock (snapshot)
            {
                if (!snapshot.IsDisposed)
                {
                    snapshot.RefCount++;
                    snapshot.Ancestors[CalculateOffset(blockHeader)] = blockHeader.Hash;
                    return snapshotId;
                }
            }
        }

        return CreateSnapshot(blockHeader);
    }

    private enum SnapshotCompatibility
    {
        Reuse,
        NewSegment,
        Fork,
        Orphaned,
    }

    // Check if we can reuse parent's snapshot
    private SnapshotCompatibility CheckSnapshotCompatibility(int parentSnapshotId, BlockHeader blockHeader, out AncestorSnapshot snapshot)
    {
        if (!_snapshots.TryGetValue(parentSnapshotId, out snapshot))
            return SnapshotCompatibility.Orphaned;

        // Check if this block would overflow the current segment
        long blockNumberInSegment = blockHeader.Number - snapshot.BaseBlockNumber;
        if (blockNumberInSegment >= SegmentSize)
        {
            // Need new segment - this snapshot is full
            return SnapshotCompatibility.NewSegment;
        }

        // Check for conflicts (fork detection)
        Hash256? existingHash = snapshot.Ancestors[CalculateOffset(blockHeader)];

        return existingHash is null || existingHash == blockHeader.Hash
            ? SnapshotCompatibility.Reuse // No conflict - can reuse
            : SnapshotCompatibility.Fork; // Conflict - fork detected
    }

    private int CreateSnapshot(BlockHeader blockHeader, int? parentSnapshotId = null)
    {
        Hash256?[] array = ArrayPool<Hash256?>.Shared.Rent(SegmentSize);
        array.AsSpan(0, SegmentSize).Clear();
        return CreateSnapshot(blockHeader, (blockHeader.Number / SegmentSize) * SegmentSize, array, parentSnapshotId);
    }

    private int CreateSnapshot(BlockHeader blockHeader, long baseBlockNumber, Hash256?[] array, int? parentSnapshotId)
    {
        array[CalculateOffset(blockHeader)] = blockHeader.Hash;
        int id = Interlocked.Increment(ref _nextSnapshotId);
        _snapshots[id] = new AncestorSnapshot(array, baseBlockNumber, parentSnapshotId);
        return id;
    }

    private static int CalculateOffset(BlockHeader blockHeader) => (int)(blockHeader.Number % SegmentSize);

    private int CreateSnapshotFromParent(BlockHeader blockHeader, AncestorSnapshot parentSnapshot)
    {
        Hash256?[] newArray = ArrayPool<Hash256?>.Shared.Rent(SegmentSize);
        parentSnapshot.Ancestors.AsSpan(0, SegmentSize).CopyTo(newArray.AsSpan(0, SegmentSize));
        return CreateSnapshot(blockHeader, parentSnapshot.BaseBlockNumber, newArray, parentSnapshot.ParentSnapshotId);
    }

    public Hash256? GetHash(BlockHeader headBlock, long depth)
    {
        if (depth == 0)
            return headBlock.Hash;

        if (!_blocks.TryGetValue(headBlock.Hash!, out BlockData blockData))
        {
            blockData = LoadFromStore(headBlock);
            if (blockData.SnapshotId == 0)
                return null;
        }

        return GetAncestorHash(blockData, depth);
    }

    private Hash256? GetAncestorHash(BlockData blockData, long depth)
    {
        long targetBlockNumber = blockData.Number - depth;
        if (targetBlockNumber < 0)
            return null;

        int currentSnapshotId = blockData.SnapshotId;

        while (currentSnapshotId != 0)
        {
            if (!_snapshots.TryGetValue(currentSnapshotId, out AncestorSnapshot snapshot))
                return null;

            if (targetBlockNumber >= snapshot.BaseBlockNumber &&
                targetBlockNumber < snapshot.BaseBlockNumber + SegmentSize)
            {
                int offset = (int)(targetBlockNumber % SegmentSize);
                return snapshot.Ancestors[offset];
            }

            if (!snapshot.ParentSnapshotId.HasValue)
            {
                return null;
            }

            currentSnapshotId = snapshot.ParentSnapshotId.Value;
        }

        return null;
    }

    private BlockData LoadFromStore(BlockHeader blockHeader)
    {
        using ArrayPoolListRef<BlockHeader> blocksToAdd = new(SegmentSize);
        BlockHeader? currentHeader = blockHeader;

        for (int i = 0; i < SegmentSize; i++)
        {
            if (_blocks.ContainsKey(currentHeader.Hash!))
                break;

            blocksToAdd.Add(currentHeader);

            if (currentHeader.ParentHash is null)
                break;

            BlockHeader? parent = headerStore.Get(currentHeader.ParentHash, true, currentHeader.Number - 1);
            if (parent is null)
                break;

            currentHeader = parent;
        }

        while (blocksToAdd.Count > 0)
        {
            BlockHeader? header = blocksToAdd.RemoveLast();
            if (!_blocks.ContainsKey(header.Hash!))
            {
                Set(header);
            }
        }

        _blocks.TryGetValue(blockHeader.Hash!, out BlockData result);
        return result;
    }

    public Task Prefetch(BlockHeader blockHeader)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!_blocks.TryGetValue(blockHeader.Hash!, out BlockData blockData) || !HasAllAncestors(blockData))
                {
                    LoadFromStore(blockHeader);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn) _logger.Warn($"Background fetch failed for block {blockHeader.Number}: {ex.Message}");
            }
        });
    }

    private bool HasAllAncestors(BlockData block) => GetAncestorHash(block, SegmentSize) is not null;

    public void Remove(Hash256AsKey blockHash)
    {
        if (_blocks.TryRemove(blockHash, out BlockData block)
            && _snapshots.TryGetValue(block.SnapshotId, out AncestorSnapshot snapshot))
        {
            lock (snapshot)
            {
                snapshot.RefCount--;
                if (snapshot.RefCount == 0)
                {
                    _snapshots.TryRemove(block.SnapshotId, out _);
                    snapshot.Dispose();
                }
            }
        }
    }

    public int PruneBefore(long blockNumber)
    {
        int removed = 0;

        int capacity = blockNumber > _minBlock ? (int)(blockNumber - _minBlock) : 64;
        _minBlock = blockNumber;
        using ArrayPoolListRef<Hash256> blocksToRemove = new(capacity);

        foreach (KeyValuePair<Hash256AsKey, BlockData> kvp in _blocks)
        {
            if (kvp.Value.Number < blockNumber)
            {
                blocksToRemove.Add(kvp.Key);
            }
        }

        foreach (Hash256 hash in blocksToRemove)
        {
            Remove(hash);
            removed++;
        }

        return removed;
    }
    public bool Contains(Hash256 blockHash) => _blocks.ContainsKey(blockHash);

    public CacheStats GetStats()
    {
        int totalRefCount = 0;
        int totalSegments = 0;
        int snapshotsCount = 0;

        foreach (AncestorSnapshot snapshot in _snapshots.Values)
        {
            totalRefCount += snapshot.RefCount;
            int? parentId = snapshot.ParentSnapshotId;
            int segmentDepth = 1;
            while (parentId.HasValue && _snapshots.ContainsKey(parentId.Value))
            {
                segmentDepth++;

                if (!_snapshots.TryGetValue(parentId.Value, out AncestorSnapshot parent))
                    break;

                parentId = parent.ParentSnapshotId;
            }
            totalSegments = Math.Max(totalSegments, segmentDepth);
            snapshotsCount++;
        }

        return new CacheStats
        {
            TotalBlocks = _blocks.Count,
            UniqueSnapshots = snapshotsCount,
            AverageBlocksPerSnapshot = snapshotsCount > 0 ? (double)totalRefCount / snapshotsCount : 0,
            MaxSegmentDepth = totalSegments
        };
    }

    public void Clear()
    {
        foreach (AncestorSnapshot snapshot in _snapshots.Values)
        {
            snapshot.Dispose();
        }

        _blocks.Clear();
        _snapshots.Clear();
    }

    public void Dispose()
    {
        Clear();
    }

    private readonly struct BlockInfo(BlockHeader blockHeader)
    {
        public long Number { get; } = blockHeader.Number;
        public Hash256? ParentHash { get; } = blockHeader.ParentHash;
    }

    private readonly struct BlockData
    {
        public BlockInfo Block { get; init; }
        public long Number => Block.Number;
        public Hash256? ParentHash => Block.ParentHash;
        public int SnapshotId { get; init; }
    }

    public readonly struct CacheStats
    {
        public int TotalBlocks { get; init; }
        public int UniqueSnapshots { get; init; }
        public double AverageBlocksPerSnapshot { get; init; }
        public int MaxSegmentDepth { get; init; }

        public override string ToString() =>
            $"Blocks: {TotalBlocks}, Snapshots: {UniqueSnapshots}, Sharing: {AverageBlocksPerSnapshot:F1} blocks/snapshot, Max depth: {MaxSegmentDepth} segments ({MaxSegmentDepth * 256} blocks)";
    }

    /// <summary>
    /// Snapshot segment covering 256 blocks, can link to parent segment for deeper history
    /// Multiple blocks share segments, segments chain together for deep queries
    /// </summary>
    private class AncestorSnapshot(
        Hash256?[] ancestors,
        long baseBlockNumber,
        int? parentSnapshotId)
        : IDisposable
    {
        public Hash256?[] Ancestors { get; } = ancestors;
        public long BaseBlockNumber { get; } = baseBlockNumber;
        public int? ParentSnapshotId { get; } = parentSnapshotId;
        public int RefCount { get; set; } = 1;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                ArrayPool<Hash256?>.Shared.Return(Ancestors, clearArray: true);
                IsDisposed = true;
            }
        }
    }
}
