// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A read-only bundle of <see cref="Snapshot"/>s backed by a persistence reader.
/// </summary>
/// <param name="bufferPin">A <see cref="INodeRecordBuffer.PinAll"/> keeping every trie node buffer record the bundle's readers may
/// look up readable, released with the bundle; null when nothing reads the buffer through this bundle.</param>
/// <param name="slotFilterBitsPerKey">Bits per key of the negative filter <see cref="GetSlotFiltered"/> builds over the
/// in-memory snapshots' slots; 0 never builds one.</param>
public sealed class ReadOnlySnapshotBundle(
    SnapshotPooledList snapshots,
    IPersistence.IPersistenceReader persistenceReader,
    bool recordDetailedMetrics,
    PersistedSnapshotStack persistedSnapshots,
    IDisposable? bufferPin,
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

    /// <summary>The in-memory snapshot of this bundle's state, which resolves its tries' roots; null when the state is persisted.</summary>
    internal Snapshot? StateSnapshot => snapshots.Count > 0 ? snapshots[^1] : null;

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

    private static readonly StableStringLabel _readAccountSnapshotLabel = StableStringLabel.Get("account_snapshot");
    private static readonly StableStringLabel _readAccountPersistenceLabel = StableStringLabel.Get("account_persistence");
    private static readonly StableStringLabel _readAccountPersistenceNullLabel = StableStringLabel.Get("account_persistence_null");
    private static readonly StableStringLabel _readStorageSnapshotLabel = StableStringLabel.Get("storage_snapshot");
    private static readonly StableStringLabel _readStoragePersistenceLabel = StableStringLabel.Get("storage_persistence");
    private static readonly StableStringLabel _readStoragePersistenceNullLabel = StableStringLabel.Get("storage_persistence_null");
    private static readonly StableStringLabel _readStateNodeSnapshotLabel = StableStringLabel.Get("state_node_snapshot");
    private static readonly StableStringLabel _readStorageNodeSnapshotLabel = StableStringLabel.Get("storage_node_snapshot");
    private static readonly StableStringLabel _readStateRlpLabel = StableStringLabel.Get("state_rlp");
    private static readonly StableStringLabel _readStorageRlpLabel = StableStringLabel.Get("storage_rlp");

    /// <summary>Starts reading persisted accounts and slots ahead of use. Keys held by the in-memory snapshots are read from there later, so a prefetch of them only warms a cache.</summary>
    public void Prefetch(IPersistence.PrefetchColumns columns, Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken)
    {
        GuardDispose();
        if (persistenceReader is IPersistence.IPrefetchingReader prefetcher) prefetcher.Prefetch(columns, accounts, slots, cancellationToken);
    }

    /// <summary>Which columns of a block are worth prefetching, decided from sampled keys; see <see cref="IPersistence.IPrefetchingReader.PlanPrefetch"/>.</summary>
    public IPersistence.PrefetchColumns PlanPrefetch(Span<Address> accountSamples, Span<StorageCell> slotSamples)
    {
        GuardDispose();
        return persistenceReader is IPersistence.IPrefetchingReader prefetcher
            ? prefetcher.PlanPrefetch(accountSamples, slotSamples)
            : IPersistence.PrefetchColumns.None;
    }

    /// <summary>Whether <see cref="Prefetch"/> reads anything ahead from the persistence below.</summary>
    public bool SupportsPrefetch => persistenceReader is IPersistence.IPrefetchingReader { SupportsPrefetch: true };

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

        (Address address, UInt256 index) = key.Key;
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

    /// <summary>
    /// Returns exactly what <see cref="GetSlot(int, HashedKey{ValueTuple{Address, UInt256}}, out UInt256?)"/> returns,
    /// but asks a negative filter over the in-memory snapshots first, so a slot none of them wrote - nearly every slot
    /// an <c>eth_call</c> reads - costs one filter probe instead of one dictionary probe per snapshot.
    /// </summary>
    /// <remarks>
    /// For read-only execution only. The first call builds the filter inline, once per bundle; reads racing that
    /// build take the plain loop instead of waiting. The build costs about a millisecond at mainnet sizes (8 layers,
    /// 75k slots) and is paid back after about 4,000 definite misses. Bundles are cached per base block for at most
    /// 15 s, so reads spread over many blocks pay one build per block and window.
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
    // persistence. Only GetSlotFiltered calls it. GetSlot keeps its own inline copy on purpose: calling this from there
    // shrinks GetSlot's IL enough for Tier1 to inline all of GetSlot into SnapshotBundle.GetSlot, which changes the
    // compiled code of every block-processing slot read and made those reads slower.
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

    /// <remarks>
    /// Only the newest snapshot is probed. Its root reaches every buffer record of this state: a block that rewrites a node
    /// carries the pointers to the records of its unchanged children over from the record it replaces, and the snapshots' pins
    /// keep those records readable. A node no buffer record holds is unchanged since the persisted state, as block
    /// processing, which reads the same records and falls back to persistence, relies on. An older snapshot's root reaches
    /// only the records of that older state, so probing it after a miss finds nothing newer.
    /// </remarks>
    public bool TryFindStateNodes(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        if (snapshots.Count > 0 && snapshots[^1].TryGetStateNode(key, out node))
        {
            Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
            if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateNodeSnapshotLabel);
            return true;
        }

        node = null;
        return false;
    }

    // Note: No self-destruct boundary check needed for trie nodes. Trie iteration starts from the storage root hash,
    // so if storage was self-destructed, the new root is different and orphaned nodes won't be traversed.
    public bool TryFindStorageNodes(Hash256 address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) =>
        TryFindStorageNodes((address, path), out node);

    /// <remarks>
    /// Only the newest snapshot holding a storage root entry for the account (0 when the storage was cleared) is probed, as <see cref="TryFindStateNodes(HashedKey{TreePath}, out TrieNode?)"/>
    /// probes only the newest one: it holds the root of the current storage trie, and older snapshots hold older versions of it.
    /// </remarks>
    public bool TryFindStorageNodes(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (!snapshots[i].TryGetStorageRoot(key.Key.Item1, out ulong root)) continue;

            if (snapshots[i].TryGetStorageNode(root, key.Key.Item2, out node))
            {
                Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageNodeSnapshotLabel);
                return true;
            }

            break;
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

    /// <summary>Passes the persisted RLP of a trie node to <paramref name="action"/>, borrowed for the duration of the call.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <returns>Whether the node was found and passed.</returns>
    public bool TryLoadPersistedRlp<TArg>(Hash256? address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
    {
        GuardDispose();

        if (_persistedSnapshotCount > 0 && (address is null
                ? persistedSnapshots.TryLoadStateRlp(in path, action, arg)
                : persistedSnapshots.TryLoadStorageRlp(address, in path, action, arg))) return true;

        Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        bool found = address is null
            ? persistenceReader.TryLoadStateRlp(path, flags, action, arg)
            : persistenceReader.TryLoadStorageRlp(address, path, flags, action, arg);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, address is null ? _readStateRlpLabel : _readStorageRlpLabel);

        return found;
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
        bufferPin?.Dispose();
    }
}
