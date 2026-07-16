// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>
/// The per-scope read/write view of one state: an optional write buffer for the block being
/// processed, the leased chain of in-memory diff layers (oldest first), and a leased persistence
/// reader as the final fallthrough.
/// </summary>
/// <remarks>
/// The chain is ordered oldest first and walked backwards, so appending a newly sealed layer is an
/// O(1) <see cref="PbtSnapshotPooledList.Add"/> that leaves every existing index untouched.
/// </remarks>
/// <param name="snapshots">Leased layer chain, oldest first; the bundle takes ownership of the leases.</param>
/// <param name="reader">Leased persistence reader; the bundle takes ownership.</param>
/// <param name="resourcePool">Pool the write buffer is rented from and returned to.</param>
/// <param name="usage">Category to rent the write buffer from; also the category every layer this
/// bundle seals returns its content to.</param>
public class PbtSnapshotBundle(
    PbtSnapshotPooledList snapshots,
    IPbtPersistence.IReader reader,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage,
    bool isReadOnly) : IDisposable
{
    private PbtSnapshotContent? _writeBuffer = isReadOnly ? null : resourcePool.GetSnapshotContent(usage);
    private bool _isDisposed;

    private PbtSnapshotContent WriteBuffer => _writeBuffer ?? throw new InvalidOperationException("The bundle is read-only");

    public Account? GetAccount(Address address)
    {
        AddressAsKey key = address;
        if (_writeBuffer is not null && _writeBuffer.Accounts.TryGetValue(key, out Account? account)) return account;

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.Accounts.TryGetValue(key, out account)) return account;
        }

        return reader.GetAccount(address);
    }

    /// <summary>Returns the slot value (zero when absent or self-destructed). The walk stops at a self-destruct marker.</summary>
    public EvmWord GetSlot(Address address, in UInt256 slot)
    {
        AddressAsKey key = address;
        (AddressAsKey, UInt256) slotKey = (key, slot);
        if (_writeBuffer is not null)
        {
            if (_writeBuffer.Slots.TryGetValue(slotKey, out EvmWord value)) return value;
            if (_writeBuffer.SelfDestructs.ContainsKey(key)) return default;
        }

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            PbtSnapshotContent content = snapshots[i].Content;
            if (content.Slots.TryGetValue(slotKey, out EvmWord value)) return value;
            if (content.SelfDestructs.ContainsKey(key)) return default;
        }

        return reader.GetSlot(address, slot);
    }

    /// <summary>Returns the complete leaf blob of the stem, or null when the stem does not exist.</summary>
    /// <remarks>Layer/write-buffer hits are wrapped without copying (their arrays are owned by the layer);
    /// the reader fallthrough returns a pooled buffer the caller must dispose.</remarks>
    public RefCountingMemory? GetLeafBlob(in Stem stem)
    {
        if (_writeBuffer is not null && _writeBuffer.LeafBlobs.TryGetValue(stem, out byte[]? blob)) return AsFound(blob);

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.LeafBlobs.TryGetValue(stem, out blob)) return AsFound(blob);
        }

        return reader.GetLeafBlob(stem);

        // layers store an empty blob as the "stem deleted" marker, which must stop the walk
        static RefCountingMemory? AsFound(byte[] blob) => blob.Length == 0 ? null : RefCountingMemory.Wrapping(blob);
    }

    public RefCountingMemory? GetTrieNode(in TrieNodeKey key)
    {
        if (_writeBuffer is not null && _writeBuffer.TrieNodes.TryGetValue(key, out byte[]? node)) return RefCountingMemory.WrappingOrNull(node);

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            // a found null is a tombstone: the node was removed at this layer
            if (snapshots[i].Content.TrieNodes.TryGetValue(key, out node)) return RefCountingMemory.WrappingOrNull(node);
        }

        return reader.GetTrieNode(key);
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
    /// Seals the write buffer — the block's flat writes plus the root computation's blob and node
    /// results — into a snapshot, appends it as the bundle's newest layer (leased), and starts a
    /// fresh buffer for the next block.
    /// </summary>
    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to)
    {
        // ownership of the buffer passes to the snapshot, which returns it to the pool once its last
        // lease drops; the bundle rents a fresh one and must never touch the old one again
        PbtSnapshot snapshot = new(from, to, WriteBuffer, resourcePool, usage);
        snapshot.TryLease();
        snapshots.Add(snapshot);
        _writeBuffer = resourcePool.GetSnapshotContent(usage);
        return snapshot;
    }

    /// <remarks>
    /// Idempotent: the layer leases and the reader are each released exactly once however often this
    /// is called. Once <see cref="PbtSnapshot.CleanUp"/> returns pooled content, a second release
    /// would hand a live layer's content back to the pool while another scope still reads it.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true)) return;

        try
        {
            // releases one lease per layer and returns the backing array to the pool; each layer whose
            // last lease this drops returns its own content
            snapshots.Dispose();

            if (_writeBuffer is not null)
            {
                // the unsealed buffer is the bundle's own to return; null it so a stray write lands in
                // a NullReferenceException rather than in a buffer another block has already rented
                resourcePool.ReturnSnapshotContent(usage, _writeBuffer);
                _writeBuffer = null;
            }
        }
        finally
        {
            // the reader pins a native RocksDB snapshot, which pins SST files: it must be released
            // even if a layer release throws
            reader.Dispose();
        }
    }
}
