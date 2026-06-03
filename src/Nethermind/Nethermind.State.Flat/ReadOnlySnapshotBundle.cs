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
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A read-only bundle of <see cref="Snapshot"/>s backed by a persistence reader.
/// </summary>
public sealed class ReadOnlySnapshotBundle(
    SnapshotPooledList snapshots,
    IPersistence.IPersistenceReader persistenceReader,
    bool recordDetailedMetrics)
    : RefCountingDisposable
{
    public int SnapshotCount => snapshots.Count;
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
            {
                return i;
            }
        }

        return -1;
    }

    public byte[]? GetSlot(Address address, in UInt256 index, int selfDestructStateIdx) =>
        GetSlot(selfDestructStateIdx, (address, index));

    public byte[]? GetSlot(int selfDestructStateIdx, HashedKey<(Address, UInt256)> key)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out SlotValue? slotValue))
            {
                byte[]? res = slotValue?.ToEvmBytes();
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageSnapshotLabel);
                return res;
            }

            if (i <= selfDestructStateIdx)
            {
                return null;
            }
        }

        SlotValue outSlotValue = new();

        sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        persistenceReader.TryGetSlot(key.Key.Item1, key.Key.Item2, ref outSlotValue);
        byte[]? value = outSlotValue.ToEvmBytes();

        if (recordDetailedMetrics)
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

    /// <summary>
    /// Warm-only batched read for many slots of one <paramref name="address" />: resolves each slot
    /// against the in-memory snapshot chain, then issues a single coalesced persistence read for the
    /// slots that miss it. Values are not returned — the caller (TrieWarmer) only primes caches.
    /// </summary>
    public void WarmSlotBatch(Address address, ReadOnlySpan<HashedKey<(Address, UInt256)>> keys, ReadOnlySpan<ValueHash256> slotHashes, int selfDestructStateIdx, Span<ValueHash256> missScratch, Span<SlotValue> valueScratch, Span<bool> foundScratch)
    {
        GuardDispose();

        int missCount = 0;
        for (int k = 0; k < keys.Length; k++)
        {
            if (ResolveFromSnapshotChain(keys[k], selfDestructStateIdx))
            {
                continue;
            }

            missScratch[missCount++] = slotHashes[k];
        }

        if (missCount > 0)
        {
            persistenceReader.TryGetStorageBatchRaw(address.ToAccountPath, missScratch[..missCount], valueScratch[..missCount], foundScratch[..missCount]);
        }
    }

    /// <summary>
    /// Value-returning batched read for many slots of one <paramref name="address" />: mirrors
    /// <see cref="GetSlot(int, HashedKey{ValueTuple{Address, UInt256}})" />'s layered lookup but coalesces
    /// the persistence misses into a single MultiGet. Each resolved EVM value is written into
    /// <paramref name="results" /> at the matching input index; persistence misses yield <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="WarmSlotBatch" /> (which discards values and only primes the RocksDB block cache),
    /// this returns the values so the caller can populate the higher-level pre-block cache, letting the
    /// main execution thread serve repeated reads from memory instead of paying a per-slot RocksDB read.
    /// </remarks>
    public void GetSlotBatch(
        Address address,
        ReadOnlySpan<HashedKey<(Address, UInt256)>> keys,
        ReadOnlySpan<ValueHash256> slotHashes,
        int selfDestructStateIdx,
        Span<byte[]?> results)
    {
        GuardDispose();

        int n = keys.Length;
        // Indices into the input arrays for slots that miss the snapshot chain and need a persistence read.
        Span<int> missIndices = n <= 256 ? stackalloc int[n] : new int[n];
        Span<ValueHash256> missHashes = n <= 256 ? stackalloc ValueHash256[n] : new ValueHash256[n];
        int missCount = 0;

        for (int k = 0; k < n; k++)
        {
            if (TryResolveSlotFromSnapshotChain(keys[k], selfDestructStateIdx, out byte[]? value))
            {
                results[k] = value;
                continue;
            }

            missIndices[missCount] = k;
            missHashes[missCount] = slotHashes[k];
            missCount++;
        }

        if (missCount == 0) return;

        Span<SlotValue> valueScratch = missCount <= 256 ? stackalloc SlotValue[missCount] : new SlotValue[missCount];
        Span<bool> foundScratch = missCount <= 256 ? stackalloc bool[missCount] : new bool[missCount];
        persistenceReader.TryGetStorageBatchRaw(address.ToAccountPath, missHashes[..missCount], valueScratch, foundScratch);

        for (int m = 0; m < missCount; m++)
        {
            results[missIndices[m]] = foundScratch[m] ? valueScratch[m].ToEvmBytes() : null;
        }
    }

    /// <summary>
    /// Resolves <paramref name="key" /> against the snapshot chain, mirroring <see cref="ResolveFromSnapshotChain" />
    /// but returning the slot's EVM value (or <c>null</c> at a self-destruct boundary) on a hit.
    /// </summary>
    private bool TryResolveSlotFromSnapshotChain(HashedKey<(Address, UInt256)> key, int selfDestructStateIdx, out byte[]? value)
    {
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out SlotValue? slotValue))
            {
                value = slotValue?.ToEvmBytes();
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                value = null;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// True when the slot is resolved by the in-memory snapshot chain (a hit or a self-destruct
    /// boundary), meaning it does not need a persistence read.
    /// </summary>
    private bool ResolveFromSnapshotChain(HashedKey<(Address, UInt256)> key, int selfDestructStateIdx)
    {
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out _))
            {
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                return true;
            }
        }

        return false;
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

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte[]? value = persistenceReader.TryLoadStateRlp(path, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateRlpLabel);

        return value;
    }

    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

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

        // Null them in case unexpected mutation from trie warmer
        persistenceReader.Dispose();
    }
}
