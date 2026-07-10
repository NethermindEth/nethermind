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
    PersistedSnapshotStack persistedSnapshots)
    : RefCountingDisposable
{
    // Cached once — the persisted-snapshot stack is immutable for the bundle's lifetime. Every read
    // gates its persisted-tier probe on this being > 0, so a node with no persisted snapshots (e.g.
    // long finality disabled, or none persisted yet) skips the persisted lookups entirely.
    private readonly int _persistedSnapshotCount = persistedSnapshots.Count;
    public int SnapshotCount => _persistedSnapshotCount + snapshots.Count;
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

    /// <summary>
    /// Batched <see cref="GetAccount(Address, HashedKey{Address})"/>: walks the in-memory tiers
    /// per address and resolves the persistence-tier misses in one batched read.
    /// </summary>
    /// <remarks>Detailed per-read metrics are skipped on this path; the batch amortizes them away.</remarks>
    public void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> results)
    {
        GuardDispose();

        int count = addresses.Length;
        using ArrayPoolList<int> missIdxs = new(count);
        using ArrayPoolList<Address> missAddrs = new(count);

        for (int i = 0; i < count; i++)
        {
            Address address = addresses[i];
            HashedKey<Address> key = new(address);
            bool found = false;
            for (int s = snapshots.Count - 1; s >= 0; s--)
            {
                if (snapshots[s].TryGetAccount(key, out Account? acc))
                {
                    results[i] = acc;
                    found = true;
                    break;
                }
            }

            if (!found && _persistedSnapshotCount > 0 && persistedSnapshots.TryGetAccount(address, out Account? persistedAccount))
            {
                results[i] = persistedAccount;
                found = true;
            }

            if (!found)
            {
                missIdxs.Add(i);
                missAddrs.Add(address);
            }
        }

        if (missIdxs.Count == 0) return;

        using ArrayPoolSpan<Account?> missResults = new(missIdxs.Count);
        persistenceReader.GetAccounts(missAddrs.AsSpan(), missResults[..missIdxs.Count]);
        for (int j = 0; j < missIdxs.Count; j++)
        {
            results[missIdxs[j]] = missResults[j];
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

            if (_persistedSnapshotCount + i <= selfDestructStateIdx)
            {
                return null;
            }
        }

        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryGetSlot(address, in index, selfDestructStateIdx, sw, out byte[]? persistedSlot))
            return persistedSlot;

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

    /// <summary>
    /// Batched <see cref="GetSlot(int, HashedKey{ValueTuple{Address, UInt256}})"/>: walks the
    /// in-memory tiers (with per-cell self-destruct short-circuits) per cell and resolves the
    /// persistence-tier misses in one batched read.
    /// </summary>
    /// <remarks>Detailed per-read metrics are skipped on this path; the batch amortizes them away.</remarks>
    public void GetSlots(ReadOnlySpan<(Address Address, UInt256 Slot)> cells, ReadOnlySpan<int> selfDestructIdxs, Span<byte[]?> results)
    {
        GuardDispose();

        int count = cells.Length;
        using ArrayPoolList<int> missIdxs = new(count);
        using ArrayPoolList<(Address, UInt256)> missCells = new(count);

        for (int i = 0; i < count; i++)
        {
            (Address address, UInt256 index) = cells[i];
            HashedKey<(Address, UInt256)> key = new((address, index));
            int selfDestructStateIdx = selfDestructIdxs[i];
            bool resolved = false;
            for (int s = snapshots.Count - 1; s >= 0; s--)
            {
                if (snapshots[s].TryGetStorage(key, out SlotValue? slotValue))
                {
                    results[i] = slotValue?.ToEvmBytes();
                    resolved = true;
                    break;
                }

                if (_persistedSnapshotCount + s <= selfDestructStateIdx)
                {
                    results[i] = null;
                    resolved = true;
                    break;
                }
            }

            if (!resolved && _persistedSnapshotCount > 0 && persistedSnapshots.TryGetSlot(address, in index, selfDestructStateIdx, 0, out byte[]? persistedSlot))
            {
                results[i] = persistedSlot;
                resolved = true;
            }

            if (!resolved)
            {
                missIdxs.Add(i);
                missCells.Add((address, index));
            }
        }

        if (missIdxs.Count == 0) return;

        using ArrayPoolSpan<SlotValue> missValues = new(missIdxs.Count);
        using ArrayPoolSpan<bool> missFound = new(missIdxs.Count);
        persistenceReader.TryGetSlots(missCells.AsSpan(), missValues[..missIdxs.Count], missFound[..missIdxs.Count]);
        for (int j = 0; j < missIdxs.Count; j++)
        {
            // Mirrors the single-read path: the found flag is ignored and a default SlotValue
            // yields the same bytes a missing slot does.
            SlotValue value = missFound[j] ? missValues[j] : default;
            results[missIdxs[j]] = value.ToEvmBytes();
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

        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryLoadStateRlp(in path, out byte[]? persistedRlp))
            return persistedRlp;

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
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

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? value = persistenceReader.TryLoadStorageRlp(address, path, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageRlpLabel);

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

        snapshots.Dispose();
        persistedSnapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        persistenceReader.Dispose();
    }
}
