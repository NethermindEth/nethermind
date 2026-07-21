// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
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
    private bool _isDisposed;

    private PbtSnapshotContent WriteBuffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _writeBuffer!;
        }
    }

    public Account? GetAccount(Address address)
    {
        AddressAsKey key = address;
        if (WriteBuffer.Accounts.TryGetValue(key, out Account? account)) return account;

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.Accounts.TryGetValue(key, out account)) return account;
        }

        return readOnlyBundle.GetAccount(address);
    }

    /// <summary>Returns the slot value; zero when absent or self-destructed.</summary>
    public EvmWord GetSlot(Address address, in UInt256 slot)
    {
        AddressAsKey key = address;
        (AddressAsKey, UInt256) slotKey = (key, slot);
        PbtSnapshotContent buffer = WriteBuffer;
        if (buffer.Slots.TryGetValue(slotKey, out EvmWord value)) return value;
        if (buffer.SelfDestructs.ContainsKey(key)) return default;

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            PbtSnapshotContent content = snapshots[i].Content;
            if (content.Slots.TryGetValue(slotKey, out value)) return value;
            if (content.SelfDestructs.ContainsKey(key)) return default;
        }

        return readOnlyBundle.GetSlot(address, slot);
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

    public void SetAccount(Address address, Account? account) => WriteBuffer.Accounts[address] = account;

    // present entry = written in this layer; a zero value is a valid write (distinct from absent)
    public void SetSlot(Address address, in UInt256 slot, in EvmWord value) =>
        WriteBuffer.Slots[(address, slot)] = value;

    /// <summary>Records a leaf blob produced by the root computation; an empty blob marks the stem deleted.</summary>
    public void SetLeafBlob(in Stem stem, byte[] blob) => WriteBuffer.LeafBlobs[stem] = blob;

    /// <summary>Records a trie node produced by the root computation; a null node marks it removed.</summary>
    public void SetTrieNode(in TrieNodeKey key, byte[]? node) => WriteBuffer.TrieNodes[key] = node;

    public void SelfDestruct(Address address)
    {
        AddressAsKey key = address;
        PbtSnapshotContent buffer = WriteBuffer;
        foreach (((AddressAsKey Address, UInt256 Slot) slotKey, _) in buffer.Slots)
        {
            if (slotKey.Address.Equals(key)) buffer.Slots.TryRemove(slotKey, out _);
        }

        buffer.SelfDestructs[key] = true;
    }

    /// <summary>
    /// Seals the write buffer into a snapshot, appends it as this branch's newest layer (leased),
    /// and starts a fresh buffer for the next block.
    /// </summary>
    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to)
    {
        // ownership of the buffer passes to the snapshot, which returns it to the pool once its last
        // lease drops; the bundle must never touch the old one again
        PbtSnapshot snapshot = new(from, to, WriteBuffer, resourcePool, usage);
        snapshot.TryLease();
        snapshots.Add(snapshot);
        _writeBuffer = resourcePool.GetSnapshotContent(usage);
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

            // null it so a racing write cannot land in a buffer another block has already rented
            PbtSnapshotContent? buffer = _writeBuffer;
            _writeBuffer = null;
            if (buffer is not null) resourcePool.ReturnSnapshotContent(usage, buffer);
        }
        finally
        {
            readOnlyBundle.Dispose();
        }
    }
}
