// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A read-only bundle of <see cref="Snapshot"/>s backed by a persistence reader.
/// </summary>
/// <param name="slotFilterBitsPerKey">Bits per key of the negative filter <see cref="GetSlotFiltered"/> builds over the
/// in-memory snapshots' slots; 0 never builds one.</param>
public sealed class ReadOnlySnapshotBundle(
    SnapshotPooledList snapshots,
    IPersistence.IPersistenceReader persistenceReader,
    bool recordDetailedMetrics,
    PersistedSnapshotStack persistedSnapshots,
    bool isHistorical = false,
    double slotFilterBitsPerKey = 0)
    : RefCountingDisposable
{
    private const int SlotFilterNotBuilt = 0;
    private const int SlotFilterBuilding = 1;
    private const int SlotFilterReady = 2;
    private const int SlotFilterSkipped = 3;

    // With one snapshot the loop is already a single dictionary probe, which the filter would not beat.
    private const int MinSnapshotsForSlotFilter = 2;

    // Cached once — the persisted-snapshot stack is immutable for the bundle's lifetime. Every read
    // gates its persisted-tier probe on this being > 0, so a node with no persisted snapshots (e.g.
    // long finality disabled, or none persisted yet) skips the persisted lookups entirely.
    private readonly int _persistedSnapshotCount = persistedSnapshots.Count;
    public int SnapshotCount => _persistedSnapshotCount + snapshots.Count;

    /// <summary>
    /// True when this bundle is backed by the finalized history index (trie-less): it serves account/storage values
    /// only and has no trie nodes, so post-block state-root recomputation must not traverse it.
    /// </summary>
    public bool IsHistorical { get; } = isHistorical;
    private bool _isDisposed;

    // Negative filter over the slot keys of all in-memory snapshots, for GetSlotFiltered. Built lazily by the first
    // filtered read, so bundles that only serve block processing never pay for it.
    private BloomFilter? _slotFilter;
    private int _slotFilterState = slotFilterBitsPerKey > 0 && snapshots.Count >= MinSnapshotsForSlotFilter
        ? SlotFilterNotBuilt
        : SlotFilterSkipped;

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

        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryGetAccount(address, out Account? persistedAccount))
            return persistedAccount;

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
                return _persistedSnapshotCount + i;
        }

        return _persistedSnapshotCount > 0 && persistedSnapshots.TryGetSelfDestruct(address, out int snapshotIdx) ? snapshotIdx : -1;
    }

    public void GetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out UInt256? value) =>
        GetSlot(selfDestructStateIdx, (address, index), out value);

    public void GetSlot(int selfDestructStateIdx, HashedKey<(Address, UInt256)> key, out UInt256? value)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out UInt256? slotValue))
            {
                value = slotValue;
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabel);
                return;
            }

            if (_persistedSnapshotCount + i <= selfDestructStateIdx)
            {
                value = null;
                return;
            }
        }

        GetSlotBelowInMemory(selfDestructStateIdx, key, sw, out value);
    }

    /// <summary>
    /// Returns exactly what <see cref="GetSlot(int, HashedKey{ValueTuple{Address, UInt256}}, out UInt256?)"/> returns,
    /// but asks a negative filter over the in-memory snapshots first, so a slot none of them wrote - nearly every slot
    /// an <c>eth_call</c> reads - costs one filter probe instead of one dictionary probe per snapshot.
    /// </summary>
    /// <remarks>
    /// For read-only execution only. The first call builds the filter inline (about a millisecond at mainnet sizes),
    /// once per bundle; reads racing that build take the plain loop instead of waiting.
    /// </remarks>
    public void GetSlotFiltered(int selfDestructStateIdx, HashedKey<(Address, UInt256)> key, out UInt256? value)
    {
        GuardDispose();

        BloomFilter? filter = Volatile.Read(ref _slotFilter)
            ?? (Volatile.Read(ref _slotFilterState) == SlotFilterNotBuilt ? TryBuildSlotFilter() : null);
        if (filter is null || filter.MightContain(Snapshot.StorageFilterKey(key)))
        {
            GetSlot(selfDestructStateIdx, key, out value);
            return;
        }

        // No in-memory snapshot holds the key, so the loop would find nothing and only its self-destruct cutoff
        // could end it; that cutoff fires at some in-memory snapshot exactly when the clear is at or above the
        // oldest one.
        if (selfDestructStateIdx >= _persistedSnapshotCount)
        {
            value = null;
            return;
        }

        GetSlotBelowInMemory(selfDestructStateIdx, key, recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0, out value);
    }

    // The rest of a slot read once the in-memory snapshots did not decide it: the persisted-snapshot tier, then
    // persistence.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetSlotBelowInMemory(int selfDestructStateIdx, in HashedKey<(Address, UInt256)> key, long sw, out UInt256? value)
    {
        (Address address, UInt256 index) = key.Key;
        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryGetSlot(address, in index, selfDestructStateIdx, sw, out value))
            return;

        UInt256 outSlotValue = default;

        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        value = persistenceReader.TryGetSlot(key.Key.Item1, key.Key.Item2, ref outSlotValue) ? outSlotValue : null;

        if (recordDetailedMetrics)
        {
            if (outSlotValue.IsZero)
            {
                Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStoragePersistenceNullLabel);
            }
            else
            {
                Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStoragePersistenceLabel);
            }
        }
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
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
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
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
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

        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryLoadStateRlp(in path, out byte[]? persistedRlp))
            return persistedRlp;

        Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? value = persistenceReader.TryLoadStateRlp(path, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateRlpLabel);

        return value;
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryLoadStorageRlp(address, in path, out byte[]? persistedRlp))
            return persistedRlp;

        Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? value = persistenceReader.TryLoadStorageRlp(address, path, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageRlpLabel);

        return value;
    }

    // Only the reader that moves the state out of NotBuilt builds. It holds a lease on this bundle, so neither the
    // snapshots it walks nor the filter it publishes can be cleaned up under it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private BloomFilter? TryBuildSlotFilter()
    {
        if (Interlocked.CompareExchange(ref _slotFilterState, SlotFilterBuilding, SlotFilterNotBuilt) != SlotFilterNotBuilt) return null;

        long start = Stopwatch.GetTimestamp();
        BloomFilter? filter = null;
        try
        {
            // A key written by several snapshots is counted once per snapshot, which only lowers the false-positive rate.
            long capacity = 0;
            for (int i = 0; i < snapshots.Count; i++) capacity += snapshots[i].StoragesCount;

            // Snapshots that wrote no slot still get a filter: every read then skips them.
            filter = new BloomFilter(Math.Max(capacity, 1), slotFilterBitsPerKey);
            for (int i = 0; i < snapshots.Count; i++) snapshots[i].AddStorageKeysTo(filter);
        }
        catch (Exception)
        {
            // The plain loop is always right, so a filter that cannot be built is just not used. That deliberately
            // includes OutOfMemoryException: the one large allocation here is the filter's native block, and a read
            // must not fail because an optional filter did not fit.
            filter?.Dispose();
            Volatile.Write(ref _slotFilterState, SlotFilterSkipped);
            Metrics.RecordInMemorySlotFilterBuildFailed();
            return null;
        }

        Volatile.Write(ref _slotFilter, filter);
        Volatile.Write(ref _slotFilterState, SlotFilterReady);
        Metrics.RecordInMemorySlotFilterBuilt(filter.DataBytes, Stopwatch.GetTimestamp() - start);
        return filter;
    }

    /// <summary>The published slot filter, or <c>null</c> while none is built.</summary>
    internal BloomFilter? SlotFilter => Volatile.Read(ref _slotFilter);

    /// <summary>
    /// <c>false</c> when <see cref="GetSlotFiltered"/> can only fall back to the plain loop: no bits per key, fewer than
    /// two in-memory snapshots, or a build that failed.
    /// </summary>
    internal bool MayFilterSlots => Volatile.Read(ref _slotFilterState) != SlotFilterSkipped;

    private void ReleaseSlotFilter()
    {
        BloomFilter? filter = Interlocked.Exchange(ref _slotFilter, null);
        if (filter is null) return;

        Metrics.RecordInMemorySlotFilterReleased(filter.DataBytes);
        filter.Dispose();
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        // Before the snapshots, whose keys the filter was built from.
        ReleaseSlotFilter();
        snapshots.Dispose();
        persistedSnapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        persistenceReader.Dispose();
    }
}
