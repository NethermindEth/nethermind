// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A read-only bundle of <see cref="Snapshot"/>s backed by a persistence reader.
/// When a <see cref="IBloomFilterManager"/> is provided, bloom segments group consecutive snapshots by block range
/// so a single bloom miss skips all snapshots in that range at once.
/// </summary>
public sealed class ReadOnlySnapshotBundle : RefCountingDisposable
{
    private readonly SnapshotPooledList _snapshots;
    private readonly IPersistence.IPersistenceReader _persistenceReader;
    private readonly bool _recordDetailedMetrics;
    private readonly BloomSegment[]? _segments;
    private bool _isDisposed;

    private static readonly StringLabel _readAccountSnapshotLabel = new("account_snapshot");
    private static readonly StringLabel _readAccountPersistenceLabel = new("account_persistence");
    private static readonly StringLabel _readAccountPersistenceNullLabel = new("account_persistence_null");
    private static readonly StringLabel _readStorageSnapshotLabel = new("storage_snapshot");
    private static readonly StringLabel _readStoragePersistenceLabel = new("storage_persistence");
    private static readonly StringLabel _readStoragePersistenceNullLabel = new("storage_persistence_null");
    private static readonly StringLabel _readStateNodeSnapshotLabel = new("state_node_snapshot");
    private static readonly StringLabel _readStorageNodeSnapshotLabel = new("storage_node_snapshot");
    private static readonly StringLabel _readStateRlpLabel = new("state_rlp");
    private static readonly StringLabel _readStorageRlpLabel = new("storage_rlp");

    public ReadOnlySnapshotBundle(
        SnapshotPooledList snapshots,
        IPersistence.IPersistenceReader persistenceReader,
        bool recordDetailedMetrics,
        IBloomFilterManager? bloomFilterManager = null,
        int bloomRangeSize = 128)
    {
        _snapshots = snapshots;
        _persistenceReader = persistenceReader;
        _recordDetailedMetrics = recordDetailedMetrics;
        // Only build segments when we have a real bloom filter manager and snapshots to work with
        _segments = bloomFilterManager is not null && snapshots.Count > 0
            ? BuildSegments(snapshots, bloomFilterManager, bloomRangeSize)
            : null;
    }

    public int SnapshotCount => _snapshots.Count;

    /// <summary>
    /// Groups consecutive snapshots whose block numbers fall in the same bloom range bucket.
    /// Snapshots are ordered oldest-first (index 0 = oldest).
    /// </summary>
    private static BloomSegment[] BuildSegments(
        SnapshotPooledList snapshots,
        IBloomFilterManager bloomFilterManager,
        int bloomRangeSize)
    {
        ArrayPoolList<BloomSegment> segments = new(Math.Max(1, snapshots.Count / 4));

        int segStart = 0;
        long currentBucket = snapshots[0].To.BlockNumber / bloomRangeSize;

        for (int i = 1; i < snapshots.Count; i++)
        {
            long bucket = snapshots[i].To.BlockNumber / bloomRangeSize;
            if (bucket != currentBucket)
            {
                IBloomFilter? bloom = GetSingleBloom(bloomFilterManager, currentBucket, bloomRangeSize);
                segments.Add(new BloomSegment(bloom, segStart, i - 1));
                segStart = i;
                currentBucket = bucket;
            }
        }

        // Final segment
        {
            IBloomFilter? bloom = GetSingleBloom(bloomFilterManager, currentBucket, bloomRangeSize);
            segments.Add(new BloomSegment(bloom, segStart, snapshots.Count - 1));
        }

        BloomSegment[] result = segments.ToArray();
        segments.Dispose();
        return result;
    }

    private static IBloomFilter? GetSingleBloom(IBloomFilterManager bloomFilterManager, long bucket, int bloomRangeSize)
    {
        long bucketStart = bucket * bloomRangeSize;
        long bucketEnd = (bucket + 1) * bloomRangeSize - 1;
        using ArrayPoolList<IBloomFilter> blooms = bloomFilterManager.GetBloomFiltersForRange(bucketStart, bucketEnd);
        return blooms.Count == 1 ? blooms[0] : null;
    }

    public Account? GetAccount(Address address)
    {
        GuardDispose();

        AddressAsKey key = address;

        long sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;

        if (_segments is not null)
        {
            ulong bloomKey = XxHash64.HashToUInt64(address.Bytes);
            for (int seg = _segments.Length - 1; seg >= 0; seg--)
            {
                BloomSegment segment = _segments[seg];
                if (segment.Bloom is not null && !segment.Bloom.MightContain(bloomKey))
                {
                    Metrics.BloomFilterSkip++;
                    continue;
                }

                Metrics.BloomFilterPass++;
                for (int i = segment.EndIdx; i >= segment.StartIdx; i--)
                {
                    if (_snapshots[i].TryGetAccount(key, out Account? acc))
                    {
                        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountSnapshotLabel);
                        return acc;
                    }
                }
            }
        }
        else
        {
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].TryGetAccount(key, out Account? acc))
                {
                    if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountSnapshotLabel);
                    return acc;
                }
            }
        }

        sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        Account? account = _persistenceReader.GetAccount(address);
        if (account is null)
        {
            if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountPersistenceNullLabel);
        }
        else
        {
            if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountPersistenceLabel);
        }

        return account;
    }

    public int DetermineSelfDestructSnapshotIdx(Address address)
    {
        if (_segments is not null)
        {
            ulong bloomKey = XxHash64.HashToUInt64(address.Bytes);
            for (int seg = _segments.Length - 1; seg >= 0; seg--)
            {
                BloomSegment segment = _segments[seg];
                if (segment.Bloom is not null && !segment.Bloom.MightContain(bloomKey))
                    continue;

                for (int i = segment.EndIdx; i >= segment.StartIdx; i--)
                {
                    if (_snapshots[i].HasSelfDestruct(address))
                        return i;
                }
            }
        }
        else
        {
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].HasSelfDestruct(address))
                    return i;
            }
        }

        return -1;
    }

    public byte[]? GetSlot(Address address, in UInt256 index, int selfDestructStateIdx)
    {
        GuardDispose();

        long sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;

        if (_segments is not null)
        {
            ulong bloomKey = XxHash64.HashToUInt64(address.Bytes);
            for (int seg = _segments.Length - 1; seg >= 0; seg--)
            {
                BloomSegment segment = _segments[seg];
                if (segment.Bloom is not null && !segment.Bloom.MightContain(bloomKey))
                {
                    // Even when skipping, check if self-destruct boundary is within this segment
                    if (selfDestructStateIdx >= segment.StartIdx && selfDestructStateIdx <= segment.EndIdx)
                        return null;
                    Metrics.BloomFilterSkip++;
                    continue;
                }

                Metrics.BloomFilterPass++;
                for (int i = segment.EndIdx; i >= segment.StartIdx; i--)
                {
                    if (_snapshots[i].TryGetStorage(address, index, out SlotValue? slotValue))
                    {
                        byte[]? res = slotValue?.ToEvmBytes();
                        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabel);
                        return res;
                    }

                    if (i <= selfDestructStateIdx)
                        return null;
                }
            }
        }
        else
        {
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].TryGetStorage(address, index, out SlotValue? slotValue))
                {
                    byte[]? res = slotValue?.ToEvmBytes();
                    if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabel);
                    return res;
                }

                if (i <= selfDestructStateIdx)
                    return null;
            }
        }

        SlotValue outSlotValue = new();

        sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        _persistenceReader.TryGetSlot(address, index, ref outSlotValue);
        byte[]? value = outSlotValue.ToEvmBytes();

        if (_recordDetailedMetrics)
        {
            if (value is null || value.IsZero())
            {
                Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStoragePersistenceNullLabel);
            }
            else
            {
                Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStoragePersistenceLabel);
            }
        }

        return value;
    }

    public bool TryFindStateNodes(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();

        long sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStateNode(path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateNodeSnapshotLabel);
                return true;
            }
        }

        node = null;
        return false;
    }

    // Note: No self-destruct boundary check needed for trie nodes. Trie iteration starts from the storage root hash,
    // so if storage was self-destructed, the new root is different and orphaned nodes won't be traversed.
    public bool TryFindStorageNodes(Hash256AsKey address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();

        long sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorageNode(address, path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageNodeSnapshotLabel);
                return true;
            }
        }

        node = null;
        return false;
    }

    public byte[]? TryLoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? value = _persistenceReader.TryLoadStateRlp(path, flags);
        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateRlpLabel);

        return value;
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? value = _persistenceReader.TryLoadStorageRlp(address, path, flags);
        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageRlpLabel);

        return value;
    }

    private void GuardDispose()
    {
        if (_isDisposed) throw new ObjectDisposedException($"{nameof(ReadOnlySnapshotBundle)} is disposed");
    }

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        _snapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        _persistenceReader.Dispose();
    }

    private readonly record struct BloomSegment(IBloomFilter? Bloom, int StartIdx, int EndIdx);
}
