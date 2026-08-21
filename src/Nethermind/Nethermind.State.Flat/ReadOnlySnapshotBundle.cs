// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A read-only bundle of <see cref="Snapshot"/>s backed by a persistence reader.
/// </summary>
public sealed class ReadOnlySnapshotBundle(
    SnapshotPooledList snapshots,
    IPersistence.IPersistenceReader persistenceReader,
    bool recordDetailedMetrics,
    PersistedSnapshotStack persistedSnapshots,
    bool isHistorical = false)
    : RefCountingDisposable
{
    // Cached once — the persisted-snapshot stack is immutable for the bundle's lifetime. Every read
    // gates its persisted-tier probe on this being > 0, so a node with no persisted snapshots (e.g.
    // long finality disabled, or none persisted yet) skips the persisted lookups entirely.
    private readonly int _persistedSnapshotCount = persistedSnapshots.Count;

    // Null when the reader cannot batch (test doubles, the no-op reader), in which case the batched reads
    // below fall back to looping the single-key ones.
    private readonly IPersistence.IBatchedPersistenceReader? _batchedReader = persistenceReader as IPersistence.IBatchedPersistenceReader;

    public int SnapshotCount => _persistedSnapshotCount + snapshots.Count;

    /// <summary>
    /// True when this bundle is backed by the finalized history index (trie-less): it serves account/storage values
    /// only and has no trie nodes, so post-block state-root recomputation must not traverse it.
    /// </summary>
    public bool IsHistorical { get; } = isHistorical;
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

    public Account? GetAccount(Address address) => GetAccount(address, address);

    public Account? GetAccount(Address address, HashedKey<Address> key)
    {
        GuardDispose();

        if (TryGetAccountFromTiers(address, key, out Account? tiered)) return tiered;

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
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

    /// <summary>
    /// Walks the in-memory snapshots (newest first) and then the persisted-snapshot tier for one account.
    /// </summary>
    /// <returns><c>true</c> when a tier answered, in which case persistence must not be consulted.</returns>
    private bool TryGetAccountFromTiers(Address address, HashedKey<Address> key, out Account? account)
    {
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetAccount(key, out account))
            {
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readAccountSnapshotLabel);
                return true;
            }
        }

        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryGetAccount(address, out account)) return true;

        account = null;
        return false;
    }

    /// <summary>
    /// Batched <see cref="GetAccount(Address, HashedKey{Address})"/>: every key is resolved against the
    /// in-memory and persisted tiers individually, and only the residual misses go to persistence — as one
    /// batched read rather than one read per key.
    /// </summary>
    /// <remarks>
    /// Falls back to the per-key path when the reader cannot batch, and while <c>recordDetailedMetrics</c> is
    /// on so the persistence-tier latency histogram keeps measuring individual reads rather than a shared
    /// batch duration.
    /// </remarks>
    public void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> results)
    {
        GuardDispose();

        int count = results.Length;
        if (recordDetailedMetrics || _batchedReader is null)
        {
            for (int i = 0; i < count; i++) results[i] = GetAccount(addresses[i]);
            return;
        }

        Address[] missAddresses = ArrayPool<Address>.Shared.Rent(count);
        Account?[] missResults = ArrayPool<Account?>.Shared.Rent(count);
        int[] missIndexes = ArrayPool<int>.Shared.Rent(count);
        try
        {
            int missCount = 0;
            for (int i = 0; i < count; i++)
            {
                Address address = addresses[i];
                if (TryGetAccountFromTiers(address, new HashedKey<Address>(address), out Account? account))
                {
                    results[i] = account;
                    continue;
                }

                missIndexes[missCount] = i;
                missAddresses[missCount++] = address;
            }

            if (missCount == 0) return;

            _batchedReader.GetAccounts(missAddresses.AsSpan(0, missCount), missResults.AsSpan(0, missCount));
            for (int i = 0; i < missCount; i++) results[missIndexes[i]] = missResults[i];
        }
        finally
        {
            ArrayPool<Address>.Shared.Return(missAddresses, clearArray: true);
            ArrayPool<Account?>.Shared.Return(missResults, clearArray: true);
            ArrayPool<int>.Shared.Return(missIndexes);
        }
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

    public byte[]? GetSlot(Address address, in UInt256 index, int selfDestructStateIdx) =>
        GetSlot(selfDestructStateIdx, (address, index));

    public byte[]? GetSlot(int selfDestructStateIdx, HashedKey<(Address, UInt256)> key)
    {
        GuardDispose();

        if (TryGetSlotFromTiers(selfDestructStateIdx, key, out byte[]? tiered)) return tiered;

        SlotValue outSlotValue = new();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
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

    /// <summary>
    /// Walks the in-memory snapshots (newest first) and then the persisted-snapshot tier for one slot,
    /// stopping at the self-destruct boundary.
    /// </summary>
    /// <remarks>
    /// The boundary comparison happens <em>after</em> probing snapshot <c>i</c>, so the destructing snapshot's
    /// own writes still win; reaching it means the slot is definitively gone and persistence must not be
    /// consulted, which is why that case reports <c>true</c> with a null result.
    /// </remarks>
    /// <returns><c>true</c> when a tier answered, in which case persistence must not be consulted.</returns>
    private bool TryGetSlotFromTiers(int selfDestructStateIdx, HashedKey<(Address, UInt256)> key, out byte[]? result)
    {
        (Address address, UInt256 index) = key.Key;
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out SlotValue? slotValue))
            {
                result = slotValue?.ToEvmBytes();
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabel);
                return true;
            }

            if (_persistedSnapshotCount + i <= selfDestructStateIdx)
            {
                result = null;
                return true;
            }
        }

        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryGetSlot(address, in index, selfDestructStateIdx, sw, out result))
            return true;

        result = null;
        return false;
    }

    /// <summary>
    /// Batched <see cref="GetSlot(int, HashedKey{ValueTuple{Address, UInt256}})"/>: every key is resolved
    /// against the in-memory and persisted tiers individually, and only the residual misses go to persistence
    /// — as one batched read rather than one read per key.
    /// </summary>
    /// <inheritdoc cref="GetAccounts" path="/remarks"/>
    public void GetSlots(ReadOnlySpan<Address> addresses, ReadOnlySpan<UInt256> slots, ReadOnlySpan<int> selfDestructIdxs, Span<byte[]?> results)
    {
        GuardDispose();

        int count = results.Length;
        if (recordDetailedMetrics || _batchedReader is null)
        {
            for (int i = 0; i < count; i++) results[i] = GetSlot(addresses[i], slots[i], selfDestructIdxs[i]);
            return;
        }

        Address[] missAddresses = ArrayPool<Address>.Shared.Rent(count);
        UInt256[] missSlots = ArrayPool<UInt256>.Shared.Rent(count);
        SlotValue[] missValues = ArrayPool<SlotValue>.Shared.Rent(count);
        bool[] missFound = ArrayPool<bool>.Shared.Rent(count);
        int[] missIndexes = ArrayPool<int>.Shared.Rent(count);
        try
        {
            int missCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (TryGetSlotFromTiers(selfDestructIdxs[i], (addresses[i], slots[i]), out byte[]? slot))
                {
                    results[i] = slot;
                    continue;
                }

                missIndexes[missCount] = i;
                missSlots[missCount] = slots[i];
                missAddresses[missCount++] = addresses[i];
            }

            if (missCount == 0) return;

            // The single-key path hands persistence a fresh zeroed SlotValue and converts it regardless of
            // the hit flag, so a miss yields ToEvmBytes()'s zero. Clear the pooled buffer to match.
            missValues.AsSpan(0, missCount).Clear();
            _batchedReader.TryGetSlots(
                missAddresses.AsSpan(0, missCount), missSlots.AsSpan(0, missCount),
                missValues.AsSpan(0, missCount), missFound.AsSpan(0, missCount));

            for (int i = 0; i < missCount; i++) results[missIndexes[i]] = missValues[i].ToEvmBytes();
        }
        finally
        {
            ArrayPool<Address>.Shared.Return(missAddresses, clearArray: true);
            ArrayPool<UInt256>.Shared.Return(missSlots);
            ArrayPool<SlotValue>.Shared.Return(missValues);
            ArrayPool<bool>.Shared.Return(missFound);
            ArrayPool<int>.Shared.Return(missIndexes);
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

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);

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
