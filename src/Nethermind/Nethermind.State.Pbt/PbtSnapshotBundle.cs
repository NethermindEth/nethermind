// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>
/// The read/write view of one processing branch: a write buffer for the block in flight, over the
/// layers this branch has sealed so far, over a shared <see cref="PbtReadOnlySnapshotBundle"/>.
/// </summary>
/// <remarks>
/// Sealed layers accumulate here rather than in the shared bundle, so that view stays immutable and
/// every other scope reading it keeps a stable chain.
/// <para>
/// The local chain is ordered oldest first and walked backwards, so appending a sealed layer is an
/// O(1) <see cref="PbtSnapshotPooledList.Add"/> that leaves every existing index alone.
/// </para>
/// </remarks>
/// <param name="snapshots">Leased layers sealed by this branch, oldest first; the bundle takes ownership of the leases.</param>
/// <param name="readOnlyBundle">The shared view below; the bundle takes ownership of one lease on it.</param>
/// <param name="resourcePool">Pool the write buffer is rented from and returned to.</param>
/// <param name="usage">Category to rent the write buffer from; also the category every layer this
/// bundle seals returns its content to.</param>
public class PbtSnapshotBundle(
    PbtSnapshotPooledList snapshots,
    PbtReadOnlySnapshotBundle readOnlyBundle,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage) : IDisposable
{
    private PbtSnapshotContent? _writeBuffer = resourcePool.GetSnapshotContent(usage);
    private PbtPendingFlatWrites? _pendingFlatWrites = resourcePool.GetPendingFlatWrites(usage);
    private bool _isDisposed;

    /// <inheritdoc cref="PbtReadOnlySnapshotBundle.TreeRoot"/>
    /// <remarks>The write buffer has no root of its own: it is unfolded until a scope seals it.</remarks>
    public ValueHash256 TreeRoot => snapshots.Count > 0 ? snapshots[^1].TreeRoot : readOnlyBundle.TreeRoot;

    private PbtSnapshotContent WriteBuffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _writeBuffer!;
        }
    }

    private PbtPendingFlatWrites PendingFlatWrites
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _pendingFlatWrites!;
        }
    }

    /// <remarks>
    /// The write buffer is read as the pending flat writes rather than as blobs: the block in flight has
    /// no blob until its fold runs. Every tier below decodes from the leaf, and is handed the stem this
    /// derived rather than the address, so the walk hashes it once however deep it goes.
    /// </remarks>
    public Account? GetAccount(Address address)
    {
        if (PendingFlatWrites.Accounts.TryGetValue(address, out Account? account)) return account;

        Stem stem = PbtKeyDerivation.AccountHeaderStem(address);
        if (WriteBuffer.LeafBlobs.TryGetValue(stem, out byte[]? blob)) return PbtLeafDecoder.DecodeAccount(blob);

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.LeafBlobs.TryGetValue(stem, out blob)) return PbtLeafDecoder.DecodeAccount(blob);
        }

        return readOnlyBundle.GetAccount(stem);
    }

    /// <summary>Returns the slot value; zero when absent or self-destructed.</summary>
    /// <inheritdoc cref="PbtReadOnlySnapshotBundle.GetSlot" path="/remarks"/>
    public EvmWord GetSlot(Address address, in UInt256 slot)
    {
        AddressAsKey key = address;
        PbtPendingFlatWrites pending = PendingFlatWrites;
        if (pending.Slots.TryGetValue((key, slot), out EvmWord value)) return value;
        if (pending.SelfDestructs.ContainsKey(key)) return default;

        Stem stem = PbtLeafDecoder.SlotStem(address, slot, out byte subIndex);
        if (WriteBuffer.LeafBlobs.TryGetValue(stem, out byte[]? blob)) return PbtLeafDecoder.DecodeSlot(blob, subIndex);

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.LeafBlobs.TryGetValue(stem, out blob)) return PbtLeafDecoder.DecodeSlot(blob, subIndex);
        }

        return readOnlyBundle.GetSlot(stem, subIndex);
    }

    /// <summary>Returns the complete leaf blob of the stem, or null when the stem does not exist.</summary>
    /// <remarks>Layer and write-buffer hits are wrapped without copying (their arrays are owned by the
    /// layer); the fallthrough may return a pooled buffer the caller must dispose.</remarks>
    public RefCountingMemory? GetLeafBlob(in Stem stem)
    {
        if (WriteBuffer.LeafBlobs.TryGetValue(stem, out byte[]? blob)) return AsFound(blob);

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.LeafBlobs.TryGetValue(stem, out blob)) return AsFound(blob);
        }

        return readOnlyBundle.GetLeafBlob(stem);

        // an empty blob is the "stem deleted" marker; it must stop the walk rather than fall through
        // and resurrect the stem from a lower tier
        static RefCountingMemory? AsFound(byte[] blob) => blob.Length == 0 ? null : RefCountingMemory.Wrapping(blob);
    }

    public RefCountingMemory? GetTrieNode(in TrieNodeKey key)
    {
        if (WriteBuffer.TrieNodes.TryGetValue(key, out byte[]? node)) return RefCountingMemory.WrappingOrNull(node);

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            // a found null is a tombstone: the node was removed at this layer
            if (snapshots[i].Content.TrieNodes.TryGetValue(key, out node)) return RefCountingMemory.WrappingOrNull(node);
        }

        return readOnlyBundle.GetTrieNode(key);
    }

    public void SetAccount(Address address, Account? account) => PendingFlatWrites.Accounts[address] = account;

    // present entry = written in this block; a zero value is a valid write (distinct from absent)
    public void SetSlot(Address address, in UInt256 slot, in EvmWord value) =>
        PendingFlatWrites.Slots[(address, slot)] = value;

    /// <summary>Records a leaf blob produced by the root computation; an empty blob marks the stem deleted.</summary>
    public void SetLeafBlob(in Stem stem, byte[] blob) => WriteBuffer.LeafBlobs[stem] = blob;

    /// <summary>Records a trie node produced by the root computation; a null node marks it removed.</summary>
    public void SetTrieNode(in TrieNodeKey key, byte[]? node) => WriteBuffer.TrieNodes[key] = node;

    /// <summary>Marks every slot of <paramref name="address"/> cleared for the rest of the block in flight.</summary>
    /// <inheritdoc cref="PbtPendingFlatWrites" path="/remarks/para[1]"/>
    public void SelfDestruct(Address address)
    {
        AddressAsKey key = address;
        PbtPendingFlatWrites pending = PendingFlatWrites;
        ConcurrentDictionary<(AddressAsKey Address, UInt256 Slot), EvmWord> pendingSlots = pending.Slots;
        foreach (((AddressAsKey Address, UInt256 Slot) slotKey, _) in pendingSlots)
        {
            if (slotKey.Address.Equals(key)) pendingSlots.TryRemove(slotKey, out _);
        }

        pending.SelfDestructs[key] = true;
    }

    /// <summary>
    /// Seals the write buffer into a snapshot, appends it as this branch's newest layer (leased),
    /// and starts a fresh buffer for the next block.
    /// </summary>
    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to, in ValueHash256 treeRoot)
    {
        // ownership of the buffer passes to the snapshot, which returns it to the pool once its last
        // lease drops; the bundle must never touch the old one again
        PbtSnapshot snapshot = new(from, to, treeRoot, WriteBuffer, resourcePool, usage);
        snapshot.TryLease();
        snapshots.Add(snapshot);
        _writeBuffer = resourcePool.GetSnapshotContent(usage);

        // the fold that preceded this seal turned every pending write into a blob on the layer just
        // sealed, so the buffer has served its purpose and holding it on would only leak the block's
        // accounts and slots into the next one's footprint
        PendingFlatWrites.Reset();
        return snapshot;
    }

    /// <remarks>
    /// Idempotent: the layer leases, the write buffer and the shared bundle's lease are each released
    /// exactly once however often this is called. A second release would return a live layer's content
    /// to the pool while another scope still reads it, and would over-release the shared bundle, whose
    /// reader pins a native RocksDB snapshot — reading through a freed one takes the process down.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;

        try
        {
            snapshots.Dispose();

            // null them so a racing write cannot land in a buffer another block has already rented
            PbtSnapshotContent? buffer = _writeBuffer;
            _writeBuffer = null;
            if (buffer is not null) resourcePool.ReturnSnapshotContent(usage, buffer);

            PbtPendingFlatWrites? pending = _pendingFlatWrites;
            _pendingFlatWrites = null;
            if (pending is not null) resourcePool.ReturnPendingFlatWrites(usage, pending);
        }
        finally
        {
            readOnlyBundle.Dispose();
        }
    }
}
