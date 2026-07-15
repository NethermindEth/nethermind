// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>
/// The per-scope read/write view of one state: an optional write buffer for the block being
/// processed, the leased chain of in-memory diff layers (newest first), and a leased persistence
/// reader as the final fallthrough.
/// </summary>
/// <param name="snapshots">Leased layer chain, newest first; the bundle takes ownership of the leases.</param>
/// <param name="reader">Leased persistence reader; the bundle takes ownership.</param>
public class PbtSnapshotBundle(List<PbtSnapshot> snapshots, IPbtPersistence.IReader reader, bool isReadOnly) : IStemTrieNodeSource, IDisposable
{
    private PbtSnapshotContent? _writeBuffer = isReadOnly ? null : new PbtSnapshotContent();

    private PbtSnapshotContent WriteBuffer => _writeBuffer ?? throw new InvalidOperationException("The bundle is read-only");

    public Account? GetAccount(Address address)
    {
        AddressAsKey key = address;
        if (_writeBuffer is not null && _writeBuffer.Accounts.TryGetValue(key, out Account? account)) return account;

        foreach (PbtSnapshot snapshot in snapshots)
        {
            if (snapshot.Content.Accounts.TryGetValue(key, out account)) return account;
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

        foreach (PbtSnapshot snapshot in snapshots)
        {
            if (snapshot.Content.Slots.TryGetValue(slotKey, out EvmWord value)) return value;
            if (snapshot.Content.SelfDestructs.ContainsKey(key)) return default;
        }

        return reader.GetSlot(address, slot);
    }

    /// <summary>Returns the complete leaf blob of the stem, or null when the stem does not exist.</summary>
    public byte[]? GetLeafBlob(in Stem stem)
    {
        if (_writeBuffer is not null && _writeBuffer.LeafBlobs.TryGetValue(stem, out byte[]? blob)) return AsFound(blob);

        foreach (PbtSnapshot snapshot in snapshots)
        {
            if (snapshot.Content.LeafBlobs.TryGetValue(stem, out blob)) return AsFound(blob);
        }

        return reader.GetLeafBlob(stem);

        // layers store an empty blob as the "stem deleted" marker, which must stop the walk
        static byte[]? AsFound(byte[] blob) => blob.Length == 0 ? null : blob;
    }

    public byte[]? GetTrieNode(in TrieNodeKey key)
    {
        if (_writeBuffer is not null && _writeBuffer.TrieNodes.TryGetValue(key, out byte[]? node)) return node;

        foreach (PbtSnapshot snapshot in snapshots)
        {
            // a found null is a tombstone: the node was removed at this layer
            if (snapshot.Content.TrieNodes.TryGetValue(key, out node)) return node;
        }

        return reader.GetTrieNode(key);
    }

    public void SetAccount(Address address, Account? account) => WriteBuffer.Accounts[address] = account;

    // present entry = written in this layer; a zero value is a valid write (distinct from absent)
    public void SetSlot(Address address, in UInt256 slot, in EvmWord value) =>
        WriteBuffer.Slots[(address, slot)] = value;

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
    /// Seals the write buffer — extended with the root computation's blob and node results — into
    /// a snapshot, stacks it as the bundle's newest layer (leased), and starts a fresh buffer for
    /// the next block.
    /// </summary>
    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to, Dictionary<Stem, byte[]> leafBlobs, Dictionary<TrieNodeKey, byte[]?> trieNodes)
    {
        PbtSnapshotContent content = WriteBuffer;
        foreach ((Stem stem, byte[] blob) in leafBlobs)
        {
            content.LeafBlobs[stem] = blob;
        }

        foreach ((TrieNodeKey key, byte[]? node) in trieNodes)
        {
            content.TrieNodes[key] = node;
        }

        PbtSnapshot snapshot = new(from, to, content);
        snapshot.TryLease();
        snapshots.Insert(0, snapshot);
        _writeBuffer = new PbtSnapshotContent();
        return snapshot;
    }

    public void Dispose()
    {
        foreach (PbtSnapshot snapshot in snapshots)
        {
            snapshot.Dispose();
        }

        snapshots.Clear();
        reader.Dispose();
    }
}
