// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>
/// An immutable view of one state: a leased chain of in-memory diff layers (oldest first) over a
/// leased persistence reader. Several scopes may read one instance at once, each holding a lease.
/// </summary>
/// <remarks>
/// The reader belongs here rather than to the mutable bundle stacked above: layers spell a deleted
/// stem or removed node as a present null, both indistinguishable from never having been written
/// without consulting the dictionary, so only a tier answering from disk can tell them apart. This bundle is
/// therefore the bottom of every walk — its <see langword="null"/> and zero results are final, and
/// nothing may be stacked below it. The layer chain is never appended to, which is what makes
/// sharing safe; sealed layers accumulate on the mutable bundle instead.
/// </remarks>
/// <param name="snapshots">Leased layer chain, oldest first; the bundle takes ownership of the leases.</param>
/// <param name="reader">Leased persistence reader; the bundle takes ownership.</param>
/// <param name="storeCache">The process-wide hash-aware blob and trie-node cache.</param>
/// <param name="recordDetailedMetrics">Whether every read is timed into <see cref="Metrics.PbtReadOnlySnapshotBundleTimes"/>.</param>
public sealed class PbtReadOnlySnapshotBundle(
    PbtSnapshotPooledList snapshots,
    IPbtPersistence.IReader reader,
    PbtStoreCache storeCache,
    bool recordDetailedMetrics) : RefCountingDisposable
{
    private bool _isDisposed;

    private static readonly StringLabel _readAccountSnapshotLabel = new("account_snapshot");
    private static readonly StringLabel _readAccountPersistenceLabel = new("account_persistence");
    private static readonly StringLabel _readAccountPersistenceNullLabel = new("account_persistence_null");
    private static readonly StringLabel _readAccountPersistenceFetchLabel = new("account_persistence_fetch");
    private static readonly StringLabel _readAccountPersistenceFetchNullLabel = new("account_persistence_fetch_null");
    private static readonly StringLabel _readStorageSnapshotLabel = new("storage_snapshot");
    private static readonly StringLabel _readStoragePersistenceLabel = new("storage_persistence");
    private static readonly StringLabel _readStoragePersistenceNullLabel = new("storage_persistence_null");
    private static readonly StringLabel _readStoragePersistenceFetchLabel = new("storage_persistence_fetch");
    private static readonly StringLabel _readStoragePersistenceFetchNullLabel = new("storage_persistence_fetch_null");
    private static readonly StringLabel _readAccountLeafBlobSnapshotLabel = new("account_leaf_blob_snapshot");
    private static readonly StringLabel _readAccountLeafBlobPersistenceLabel = new("account_leaf_blob_persistence");
    private static readonly StringLabel _readAccountLeafBlobPersistenceNullLabel = new("account_leaf_blob_persistence_null");
    private static readonly StringLabel _readCodeLeafBlobSnapshotLabel = new("code_leaf_blob_snapshot");
    private static readonly StringLabel _readCodeLeafBlobPersistenceLabel = new("code_leaf_blob_persistence");
    private static readonly StringLabel _readCodeLeafBlobPersistenceNullLabel = new("code_leaf_blob_persistence_null");
    private static readonly StringLabel _readStorageLeafBlobSnapshotLabel = new("storage_leaf_blob_snapshot");
    private static readonly StringLabel _readStorageLeafBlobPersistenceLabel = new("storage_leaf_blob_persistence");
    private static readonly StringLabel _readStorageLeafBlobPersistenceNullLabel = new("storage_leaf_blob_persistence_null");
    private static readonly StringLabel _readTrieNodeSnapshotLabel = new("trie_node_snapshot");
    private static readonly StringLabel _readAccountTrieNodePersistenceLabel = new("account_trie_node_persistence");
    private static readonly StringLabel _readAccountTrieNodePersistenceNullLabel = new("account_trie_node_persistence_null");
    private static readonly StringLabel _readCodeTrieNodePersistenceLabel = new("code_trie_node_persistence");
    private static readonly StringLabel _readCodeTrieNodePersistenceNullLabel = new("code_trie_node_persistence_null");
    private static readonly StringLabel _readStorageTrieNodePersistenceLabel = new("storage_trie_node_persistence");
    private static readonly StringLabel _readStorageTrieNodePersistenceNullLabel = new("storage_trie_node_persistence_null");

    /// <summary>The three partition roots of the state this bundle views.</summary>
    public PbtPartitionRoots PartitionRoots
    {
        get
        {
            GuardDispose();
            return snapshots.Count > 0 ? snapshots[^1].PartitionRoots : reader.CurrentPartitionRoots;
        }
    }

    public ValueHash256 TreeRoot => PartitionRoots.Root;

    internal ValueHash256? GetFullLeaf(PbtFullKey key)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetFullLeaf(key, out ValueHash256? value)) return value;
        }
        return reader.GetFullLeaf(key);
    }

    /// <remarks>Decoded from the account's header stem leaf blob; see <see cref="PbtLeafDecoder"/>.</remarks>
    public Account? GetAccount(Address address) => GetAccount(PbtKeyDerivation.AccountHeaderStem(address));

    /// <inheritdoc cref="GetAccount(Address)"/>
    /// <param name="stem">The account's header stem, already derived — which costs a hash of the address.</param>
    private Account? GetAccount(in Stem stem)
    {
        GuardDispose();
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetLeafBlob(stem, out RefCountingMemory? blob))
            {
                Observe(startTimestamp, _readAccountSnapshotLabel);
                using (blob) return blob is null ? null : PbtLeafDecoder.DecodeAccount(blob.GetSpan());
            }
        }

        startTimestamp = StartTiming();
        using RefCountingMemory? persisted = reader.GetLeafBlob(stem);
        Observe(startTimestamp, persisted is null ? _readAccountPersistenceFetchNullLabel : _readAccountPersistenceFetchLabel);
        Account? account = persisted is null ? null : PbtLeafDecoder.DecodeAccount(persisted.GetSpan());
        Observe(startTimestamp, account is null ? _readAccountPersistenceNullLabel : _readAccountPersistenceLabel);
        return account;
    }

    /// <summary>Returns the slot value; zero when absent.</summary>
    /// <remarks>
    /// A layer's blob is the complete stem, so the newest layer holding one answers the read whether or
    /// not that blob carries this slot — an absent leaf there means the slot is unset, not that the walk
    /// should go on below.
    /// </remarks>
    public EvmWord GetSlot(Address address, in UInt256 slot) =>
        GetSlot(PbtLeafDecoder.SlotStem(address, slot, out byte subIndex), subIndex);

    /// <inheritdoc cref="GetSlot(Address, in UInt256)"/>
    /// <param name="stem">The stem the slot lives on, already derived — which costs up to two hashes.</param>
    /// <param name="subIndex">The slot's sub-index of <paramref name="stem"/>, as <see cref="PbtLeafDecoder.SlotStem"/> returns it.</param>
    private EvmWord GetSlot(in Stem stem, byte subIndex)
    {
        GuardDispose();
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetLeafBlob(stem, out RefCountingMemory? blob))
            {
                Observe(startTimestamp, _readStorageSnapshotLabel);
                using (blob) return blob is null ? default : PbtLeafDecoder.DecodeSlot(blob.GetSpan(), subIndex);
            }
        }

        startTimestamp = StartTiming();
        using RefCountingMemory? persisted = reader.GetLeafBlob(stem);
        Observe(startTimestamp, persisted is null ? _readStoragePersistenceFetchNullLabel : _readStoragePersistenceFetchLabel);
        EvmWord value = persisted is null ? default : PbtLeafDecoder.DecodeSlot(persisted.GetSpan(), subIndex);
        Observe(startTimestamp, EvmWordSlot.IsZero(in value) ? _readStoragePersistenceNullLabel : _readStoragePersistenceLabel);
        return value;
    }

    /// <remarks>Every non-null result is a lease the caller must dispose.</remarks>
    public RefCountingMemory? GetLeafBlob(in Stem stem) => GetLeafBlobFromSnapshotsOrPersistence(stem);

    /// <summary>Returns the leaf blob whose leaf-subtree root is <paramref name="hash"/>.</summary>
    /// <remarks>Every non-null result is a lease the caller must dispose.</remarks>
    public RefCountingMemory? GetLeafBlob(in Stem stem, in ValueHash256 hash)
    {
        GuardDispose();
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (!snapshots[i].Content.TryIsLeafBlobTombstone(stem, out bool tombstone)) continue;
            if (tombstone)
            {
                Observe(startTimestamp, LeafBlobSnapshotLabel(in stem));
                return null;
            }
            break;
        }

        if (storeCache.GetLeafBlob(stem, hash) is { } cached) return cached;

        startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetLeafBlob(stem, out RefCountingMemory? blob))
            {
                Observe(startTimestamp, LeafBlobSnapshotLabel(in stem));
                if (blob is not null) storeCache.SetLeafBlob(stem, hash, blob);
                return blob;
            }
        }

        startTimestamp = StartTiming();
        RefCountingMemory? persisted = reader.GetLeafBlob(stem);
        Observe(startTimestamp, LeafBlobPersistenceLabel(in stem, persisted is null));
        if (persisted is not null) storeCache.SetLeafBlob(stem, hash, persisted);
        return persisted;
    }

    private RefCountingMemory? GetLeafBlobFromSnapshotsOrPersistence(in Stem stem)
    {
        GuardDispose();
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetLeafBlob(stem, out RefCountingMemory? blob))
            {
                Observe(startTimestamp, LeafBlobSnapshotLabel(in stem));
                return blob;
            }
        }

        startTimestamp = StartTiming();
        RefCountingMemory? persisted = reader.GetLeafBlob(stem);
        Observe(startTimestamp, LeafBlobPersistenceLabel(in stem, persisted is null));
        return persisted;
    }

    /// <summary>The label of the zone partition a leaf blob read was served from, and whether it found one.</summary>
    /// <remarks>
    /// Leaf blobs are keyed into one column per zone, the same split the trie nodes take. It is worth
    /// more here than the tier alone: this is the read every account and slot goes through once the
    /// block's flat reads are decoded from the blob, so without the zone an account header read and a
    /// storage one — which differ by orders of magnitude in both count and blob size — share a bucket.
    /// </remarks>
    private static StringLabel LeafBlobSnapshotLabel(in Stem stem) => stem.Zone switch
    {
        PbtKeyDerivation.AccountZone => _readAccountLeafBlobSnapshotLabel,
        PbtKeyDerivation.CodeZone => _readCodeLeafBlobSnapshotLabel,
        _ => _readStorageLeafBlobSnapshotLabel,
    };

    /// <inheritdoc cref="LeafBlobSnapshotLabel"/>
    private static StringLabel LeafBlobPersistenceLabel(in Stem stem, bool isNull) => stem.Zone switch
    {
        PbtKeyDerivation.AccountZone => isNull ? _readAccountLeafBlobPersistenceNullLabel : _readAccountLeafBlobPersistenceLabel,
        PbtKeyDerivation.CodeZone => isNull ? _readCodeLeafBlobPersistenceNullLabel : _readCodeLeafBlobPersistenceLabel,
        _ => isNull ? _readStorageLeafBlobPersistenceNullLabel : _readStorageLeafBlobPersistenceLabel,
    };

    /// <remarks>Every non-null result is a lease the caller must dispose.</remarks>
    public RefCountingMemory? GetTrieNode(in TrieNodeKey key)
    {
        GuardDispose();
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetTrieNode(key, out RefCountingMemory? node))
            {
                Observe(startTimestamp, _readTrieNodeSnapshotLabel);
                return node;
            }
        }

        startTimestamp = StartTiming();
        RefCountingMemory? persisted = reader.GetTrieNode(key);
        Observe(startTimestamp, TrieNodePersistenceLabel(in key, persisted is null));
        return persisted;
    }

    /// <summary>Returns the trie-node group that represents <paramref name="hash"/>.</summary>
    /// <remarks>Every non-null result is a lease the caller must dispose.</remarks>
    public RefCountingMemory? GetTrieNode(in TrieNodeKey key, in ValueHash256 hash)
    {
        GuardDispose();
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (!snapshots[i].Content.TryIsTrieNodeTombstone(key, out bool tombstone)) continue;
            if (tombstone)
            {
                Observe(startTimestamp, _readTrieNodeSnapshotLabel);
                return null;
            }
            break;
        }

        if (storeCache.GetTrieNode(key, hash) is { } cached) return cached;

        startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetTrieNode(key, out RefCountingMemory? node))
            {
                Observe(startTimestamp, _readTrieNodeSnapshotLabel);
                if (node is not null) storeCache.SetTrieNode(key, hash, node);
                return node;
            }
        }

        startTimestamp = StartTiming();
        RefCountingMemory? persisted = reader.GetTrieNode(key);
        Observe(startTimestamp, TrieNodePersistenceLabel(in key, persisted is null));
        if (persisted is not null) storeCache.SetTrieNode(key, hash, persisted);
        return persisted;
    }

    /// <summary>The label of the partition a trie node read went down to, and whether it found one.</summary>
    /// <remarks>
    /// Trie nodes are keyed into one column per zone, and the three are worth telling apart: the account
    /// column is rewritten from the root down every block, the storage one takes most of the writes, and
    /// the code one is read far more than it is written. A reserved zone never reaches here — the read
    /// above routes by the same zone and rejects one.
    /// </remarks>
    private static StringLabel TrieNodePersistenceLabel(in TrieNodeKey key, bool isNull) => key.Path.Zone switch
    {
        PbtKeyDerivation.AccountZone => isNull ? _readAccountTrieNodePersistenceNullLabel : _readAccountTrieNodePersistenceLabel,
        PbtKeyDerivation.CodeZone => isNull ? _readCodeTrieNodePersistenceNullLabel : _readCodeTrieNodePersistenceLabel,
        _ => isNull ? _readStorageTrieNodePersistenceNullLabel : _readStorageTrieNodePersistenceLabel,
    };

    private long StartTiming() => recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;

    private void Observe(long startTimestamp, StringLabel type)
    {
        if (recordDetailedMetrics) Metrics.PbtReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - startTimestamp, type);
    }

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        try
        {
            snapshots.Dispose();
        }
        finally
        {
            // Release the native RocksDB snapshot even if a layer release throws.
            reader.Dispose();
        }
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);
}
