// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.BlockRangeTrieForest;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat;

/// <summary>
/// A read-only bundle of <see cref="Snapshot"/>s backed by a persistence reader.
/// </summary>
public sealed class ReadOnlySnapshotBundle(
    SnapshotPooledList snapshots,
    IPersistence.IPersistenceReader persistenceReader,
    bool recordDetailedMetrics,
    PersistedSnapshotList persistedSnapshots,
    IBlockRangeTrieForest blockRangeTrieForest,
    int blockRangePerForest)
    : RefCountingDisposable
{
    public int SnapshotCount => persistedSnapshots.Count + snapshots.Count;
    private bool _isDisposed;

    private static readonly StringLabel _readAccountSnapshotLabel = new("account_snapshot");
    private static readonly StringLabel _readAccountPersistedLabel = new("account_persisted_snapshot");
    private static readonly StringLabel _readAccountPersistenceLabel = new("account_persistence");
    private static readonly StringLabel _readAccountPersistenceNullLabel = new("account_persistence_null");
    private static readonly StringLabel _readStorageSnapshotLabel = new("storage_snapshot");
    private static readonly StringLabel _readStoragePersistedLabel = new("storage_persisted_snapshot");
    private static readonly StringLabel _readStoragePersistenceLabel = new("storage_persistence");
    private static readonly StringLabel _readStoragePersistenceNullLabel = new("storage_persistence_null");
    private static readonly StringLabel _readStateNodeSnapshotLabel = new("state_node_snapshot");
    private static readonly StringLabel _readStorageNodeSnapshotLabel = new("storage_node_snapshot");
    private static readonly StringLabel _readStateRlpLabel = new("state_rlp");
    private static readonly StringLabel _readStateRlpPersistedLabel = new("state_rlp_persisted_snapshot");
    private static readonly StringLabel _readStorageRlpLabel = new("storage_rlp");
    private static readonly StringLabel _readStorageRlpPersistedLabel = new("storage_rlp_persisted_snapshot");

    private static readonly Histogram _persistedSnapshotSkipTime = Prometheus.Metrics.CreateHistogram(
        "readonly_snapshot_bundle_skip_time", "skip time", new HistogramConfiguration()
        {
            LabelNames = ["part"],
            Buckets = Histogram.PowersOfTenDividedBuckets(0, 10, 10)
        });

    public Account? GetAccount(Address address) => GetAccount(address, address);

    public Account? GetAccount(Address address, HashedKey<Address> key)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetAccount(key, out Account? acc))
            {
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountSnapshotLabel);
                return acc;
            }
        }

        // Check persisted snapshots (newest-first)
        long psw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = persistedSnapshots.Count - 1; i >= 0; i--)
        {
            if (persistedSnapshots[i].TryGetAccount(address, out ReadOnlySpan<byte> rlp))
            {
                if (rlp.Length == 0)
                {
                    return null;
                }
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - psw, _readAccountPersistedLabel);
                Rlp.ValueDecoderContext ctx = new(rlp);
                return AccountDecoder.Slim.Decode(ref ctx);
            }
        }
        _persistedSnapshotSkipTime.WithLabels("account").Observe(Stopwatch.GetTimestamp() - psw);

        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        Account? account = persistenceReader.GetAccount(address);
        if (account == null)
        {
            if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountPersistenceNullLabel);
        }
        else
        {
            if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountPersistenceLabel);
        }

        return account;
    }

    public int DetermineSelfDestructSnapshotIdx(Address address)
    {
        HashedKey<Address> key = new(address);
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].HasSelfDestruct(key))
                return persistedSnapshots.Count + i;
        }

        for (int i = persistedSnapshots.Count - 1; i >= 0; i--)
        {
            bool? flag = persistedSnapshots[i].TryGetSelfDestructFlag(address);
            if (flag.HasValue)
                return i;
        }

        return -1;
    }

    public byte[]? GetSlot(Address address, in UInt256 index, int selfDestructStateIdx) =>
        GetSlot(selfDestructStateIdx, (address, index));

    public byte[]? GetSlot(int selfDestructStateIdx, HashedKey<(Address, UInt256)> key)
    {
        GuardDispose();

        (Address address, UInt256 index) = key.Key;
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out SlotValue? slotValue))
            {
                byte[]? res = slotValue?.ToEvmBytes();
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabel);
                return res;
            }

            if (persistedSnapshots.Count + i <= selfDestructStateIdx)
            {
                return null;
            }
        }

        long psw = Stopwatch.GetTimestamp();
        // Check persisted snapshots (newest-first) with self-destruct boundary
        for (int i = persistedSnapshots.Count - 1; i >= 0; i--)
        {
            if (persistedSnapshots[i].TryGetSlot(address, index, out ReadOnlySpan<byte> value))
            {
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStoragePersistedLabel);
                return value.ToArray();
            }

            if (i <= selfDestructStateIdx)
            {
                return null;
            }
        }
        _persistedSnapshotSkipTime.WithLabels("slot").Observe(Stopwatch.GetTimestamp() - psw);

        SlotValue outSlotValue = new();

        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        persistenceReader.TryGetSlot(key.Key.Item1, key.Key.Item2, ref outSlotValue);
        byte[]? slotResult = outSlotValue.ToEvmBytes();

        if (recordDetailedMetrics)
        {
            if (slotResult is null || slotResult.IsZero())
            {
                Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStoragePersistenceNullLabel);
            }
            else
            {
                Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStoragePersistenceLabel);
            }
        }

        return slotResult;
    }

    public bool TryFindStateNodes(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) =>
        TryFindStateNodes(path, out node);

    public bool TryFindStateNodes(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStateNode(key, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateNodeSnapshotLabel);
                return true;
            }
        }

        node = null;
        return false;
    }

    // Note: No self-destruct boundary check needed for trie nodes. Trie iteration starts from the storage root hash,
    // so if storage was self-destructed, the new root is different and orphaned nodes won't be traversed.
    public bool TryFindStorageNodes(Hash256 address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) =>
        TryFindStorageNodes((address, path), out node);

    public bool TryFindStorageNodes(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorageNode(key, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageNodeSnapshotLabel);
                return true;
            }
        }

        node = null;
        return false;
    }

    public byte[]? TryLoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        // RLP lives entirely in BlockRangeTrieForest for hash-only persisted snapshots.
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? forestRlp = TryLoadStateRlpFromForest(path, hash);
        if (forestRlp is not null)
        {
            if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateRlpPersistedLabel);
            return forestRlp;
        }

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? value = persistenceReader.TryLoadStateRlp(path, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateRlpLabel);

        return value;
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? forestRlp = TryLoadStorageRlpFromForest(address, path, hash);
        if (forestRlp is not null)
        {
            if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageRlpPersistedLabel);
            return forestRlp;
        }

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? value = persistenceReader.TryLoadStorageRlp(address, path, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageRlpLabel);

        return value;
    }

    private byte[]? TryLoadStateRlpFromForest(in TreePath path, Hash256 hash)
    {
        if (persistedSnapshots.Count == 0 || blockRangePerForest == 0) return null;

        long upperRange = BlockRangeForestKey.BlockRangeForBlock(persistedSnapshots[^1].To.BlockNumber, blockRangePerForest);
        long lowerRange = BlockRangeForestKey.BlockRangeForBlock(persistenceReader.CurrentState.BlockNumber, blockRangePerForest);
        ValueHash256 valueHash = hash;

        for (long r = upperRange; r >= lowerRange; r--)
        {
            byte[]? rlp = blockRangeTrieForest.TryGetState(r, path, valueHash);
            if (rlp is not null) return rlp;
        }
        return null;
    }

    private byte[]? TryLoadStorageRlpFromForest(Hash256 address, in TreePath path, Hash256 hash)
    {
        if (persistedSnapshots.Count == 0 || blockRangePerForest == 0) return null;

        long upperRange = BlockRangeForestKey.BlockRangeForBlock(persistedSnapshots[^1].To.BlockNumber, blockRangePerForest);
        long lowerRange = BlockRangeForestKey.BlockRangeForBlock(persistenceReader.CurrentState.BlockNumber, blockRangePerForest);
        ValueHash256 addrHash = address;
        ValueHash256 valueHash = hash;

        for (long r = upperRange; r >= lowerRange; r--)
        {
            byte[]? rlp = blockRangeTrieForest.TryGetStorage(r, addrHash, path, valueHash);
            if (rlp is not null) return rlp;
        }
        return null;
    }

    private void GuardDispose()
    {
        if (_isDisposed) throw new ObjectDisposedException($"{nameof(ReadOnlySnapshotBundle)} is disposed");
    }

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        snapshots.Dispose();
        persistedSnapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        persistenceReader.Dispose();
    }
}
