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
/// stem as an empty blob and a removed node as a present null, both indistinguishable from never
/// having been written, so only a tier answering from disk can tell them apart. This bundle is
/// therefore the bottom of every walk — its <see langword="null"/> and zero results are final, and
/// nothing may be stacked below it. The layer chain is never appended to, which is what makes
/// sharing safe; sealed layers accumulate on the mutable bundle instead.
/// </remarks>
/// <param name="snapshots">Leased layer chain, oldest first; the bundle takes ownership of the leases.</param>
/// <param name="reader">Leased persistence reader; the bundle takes ownership.</param>
/// <param name="recordDetailedMetrics">Whether every read is timed into <see cref="Metrics.PbtReadOnlySnapshotBundleTimes"/>.</param>
public sealed class PbtReadOnlySnapshotBundle(PbtSnapshotPooledList snapshots, IPbtPersistence.IReader reader, bool recordDetailedMetrics) : RefCountingDisposable
{
    private bool _isDisposed;

    private static readonly StringLabel _readAccountSnapshotLabel = new("account_snapshot");
    private static readonly StringLabel _readAccountPersistenceLabel = new("account_persistence");
    private static readonly StringLabel _readAccountPersistenceNullLabel = new("account_persistence_null");
    private static readonly StringLabel _readStorageSnapshotLabel = new("storage_snapshot");
    private static readonly StringLabel _readStoragePersistenceLabel = new("storage_persistence");
    private static readonly StringLabel _readStoragePersistenceNullLabel = new("storage_persistence_null");
    private static readonly StringLabel _readLeafBlobSnapshotLabel = new("leaf_blob_snapshot");
    private static readonly StringLabel _readLeafBlobPersistenceLabel = new("leaf_blob_persistence");
    private static readonly StringLabel _readLeafBlobPersistenceNullLabel = new("leaf_blob_persistence_null");
    private static readonly StringLabel _readTrieNodeSnapshotLabel = new("trie_node_snapshot");
    private static readonly StringLabel _readTrieNodePersistenceLabel = new("trie_node_persistence");
    private static readonly StringLabel _readTrieNodePersistenceNullLabel = new("trie_node_persistence_null");

    /// <summary>The EIP-8297 root of the state this bundle views, which the header root it was gathered by does not carry.</summary>
    public ValueHash256 TreeRoot
    {
        get
        {
            GuardDispose();
            return snapshots.Count > 0 ? snapshots[^1].TreeRoot : reader.CurrentTreeRoot;
        }
    }

    public Account? GetAccount(Address address)
    {
        GuardDispose();
        AddressAsKey key = address;
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.Accounts.TryGetValue(key, out Account? account))
            {
                Observe(startTimestamp, _readAccountSnapshotLabel);
                return account;
            }
        }

        startTimestamp = StartTiming();
        Account? persisted = reader.GetAccount(address);
        Observe(startTimestamp, persisted is null ? _readAccountPersistenceNullLabel : _readAccountPersistenceLabel);
        return persisted;
    }

    /// <summary>Returns the slot value; zero when absent or self-destructed.</summary>
    public EvmWord GetSlot(Address address, in UInt256 slot)
    {
        GuardDispose();
        AddressAsKey key = address;
        (AddressAsKey, UInt256) slotKey = (key, slot);
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            PbtSnapshotContent content = snapshots[i].Content;
            if (content.Slots.TryGetValue(slotKey, out EvmWord value))
            {
                Observe(startTimestamp, _readStorageSnapshotLabel);
                return value;
            }

            if (content.SelfDestructs.ContainsKey(key))
            {
                Observe(startTimestamp, _readStorageSnapshotLabel);
                return default;
            }
        }

        startTimestamp = StartTiming();
        EvmWord persisted = reader.GetSlot(address, slot);
        Observe(startTimestamp, EvmWordSlot.IsZero(in persisted) ? _readStoragePersistenceNullLabel : _readStoragePersistenceLabel);
        return persisted;
    }

    /// <remarks>Layer hits wrap the layer-owned array without copying; the reader fallthrough returns a
    /// pooled buffer the caller must dispose.</remarks>
    public RefCountingMemory? GetLeafBlob(in Stem stem)
    {
        GuardDispose();
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            // layers store an empty blob as the "stem deleted" marker, which must stop the walk
            if (snapshots[i].Content.LeafBlobs.TryGetValue(stem, out byte[]? blob))
            {
                Observe(startTimestamp, _readLeafBlobSnapshotLabel);
                return blob.Length == 0 ? null : RefCountingMemory.Wrapping(blob);
            }
        }

        startTimestamp = StartTiming();
        RefCountingMemory? persisted = reader.GetLeafBlob(stem);
        Observe(startTimestamp, persisted is null ? _readLeafBlobPersistenceNullLabel : _readLeafBlobPersistenceLabel);
        return persisted;
    }

    public RefCountingMemory? GetTrieNode(in TrieNodeKey key)
    {
        GuardDispose();
        long startTimestamp = StartTiming();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            // a found null is a tombstone: the node was removed at this layer
            if (snapshots[i].Content.TrieNodes.TryGetValue(key, out byte[]? node))
            {
                Observe(startTimestamp, _readTrieNodeSnapshotLabel);
                return RefCountingMemory.WrappingOrNull(node);
            }
        }

        startTimestamp = StartTiming();
        RefCountingMemory? persisted = reader.GetTrieNode(key);
        Observe(startTimestamp, persisted is null ? _readTrieNodePersistenceNullLabel : _readTrieNodePersistenceLabel);
        return persisted;
    }

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
            // the reader pins a native RocksDB snapshot, which pins SST files: it must be released
            // even if a layer release throws
            reader.Dispose();
        }
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);
}
