// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
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
    private static readonly StringLabel _readStateRlpWarmerLabel = new("state_rlp_warmer");
    private static readonly StringLabel _readStorageRlpWarmerLabel = new("storage_rlp_warmer");

    public Account? GetAccount(Address address)
    {
        GuardDispose();

        AddressAsKey key = address;

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
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].HasSelfDestruct(address))
            {
                return i;
            }
        }

        return -1;
    }

    public byte[]? GetSlot(Address address, in UInt256 index, int selfDestructStateIdx)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(address, index, out SlotValue? slotValue))
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
        persistenceReader.TryGetSlot(address, index, ref outSlotValue);
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

    public bool TryFindStateNodes(in TreePath path, Hash256 hash, [NotNullWhen(true)] out RefCountingTrieNode? node)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStateNode(path, out RefCountingTrieNode? refNode) && refNode.TryAcquireLease())
            {
                node = refNode;
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
    public bool TryFindStorageNodes(Hash256AsKey address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out RefCountingTrieNode? node)
    {
        GuardDispose();

        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorageNode(address, path, out RefCountingTrieNode? refNode) && refNode.TryAcquireLease())
            {
                node = refNode;
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageNodeSnapshotLabel);
                return true;
            }
        }

        node = null;
        return false;
    }

    public int TryLoadStateRlpFromPersistence(in TreePath path, Hash256 hash, Span<byte> destination, ReadFlags flags)
    {
        GuardDispose();

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        int len = persistenceReader.TryLoadStateRlp(path, destination, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateRlpLabel);

        return len;
    }

    public int TryLoadStorageRlpFromPersistence(Hash256 address, in TreePath path, Hash256 hash, Span<byte> destination, ReadFlags flags)
    {
        GuardDispose();

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        int len = persistenceReader.TryLoadStorageRlp(address, path, destination, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageRlpLabel);

        return len;
    }

    public int TryLoadStateRlpForWarmer(in TreePath path, Hash256 hash, Span<byte> destination, ReadFlags flags)
    {
        GuardDispose();

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        int len = persistenceReader.TryLoadStateRlp(path, destination, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateRlpWarmerLabel);

        return len;
    }

    public int TryLoadStorageRlpForWarmer(Hash256 address, in TreePath path, Hash256 hash, Span<byte> destination, ReadFlags flags)
    {
        GuardDispose();

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        int len = persistenceReader.TryLoadStorageRlp(address, path, destination, flags);
        if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageRlpWarmerLabel);

        return len;
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
