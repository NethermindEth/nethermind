// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>
/// A complete, immutable view of one state: the leased chain of in-memory diff layers (oldest first)
/// over a leased persistence reader. Shareable — several scopes may read one instance at once, each
/// holding a lease.
/// </summary>
/// <remarks>
/// The reader belongs here rather than to the mutable bundle stacked above, and that placement is
/// what keeps the reads honest: layers spell a deleted stem as an empty blob and a removed node as a
/// present null, both of which read back as "nothing", indistinguishable from never having been
/// written. Only a tier that answers from disk itself can tell the difference, so this bundle is the
/// bottom of every walk — its <see langword="null"/> and zero results are final, and nothing may be
/// stacked below it.
/// <para>
/// Immutable for its whole lifetime, which is what makes it safe to share: the layer chain is never
/// appended to, so a reader walking it cannot see it shift. Sealed layers accumulate on the mutable
/// bundle instead.
/// </para>
/// </remarks>
/// <param name="snapshots">Leased layer chain, oldest first; the bundle takes ownership of the leases.</param>
/// <param name="reader">Leased persistence reader; the bundle takes ownership.</param>
public sealed class PbtReadOnlySnapshotBundle(PbtSnapshotPooledList snapshots, IPbtPersistence.IReader reader) : RefCountingDisposable
{
    private bool _isDisposed;

    public Account? GetAccount(Address address)
    {
        GuardDispose();
        AddressAsKey key = address;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.Accounts.TryGetValue(key, out Account? account)) return account;
        }

        return reader.GetAccount(address);
    }

    /// <summary>Returns the slot value (zero when absent or self-destructed). The walk stops at a self-destruct marker.</summary>
    public EvmWord GetSlot(Address address, in UInt256 slot)
    {
        GuardDispose();
        AddressAsKey key = address;
        (AddressAsKey, UInt256) slotKey = (key, slot);
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            PbtSnapshotContent content = snapshots[i].Content;
            if (content.Slots.TryGetValue(slotKey, out EvmWord value)) return value;
            if (content.SelfDestructs.ContainsKey(key)) return default;
        }

        return reader.GetSlot(address, slot);
    }

    /// <summary>Returns the complete leaf blob of the stem, or null when the stem does not exist.</summary>
    /// <remarks>Layer hits are wrapped without copying (their arrays are owned by the layer); the reader
    /// fallthrough returns a pooled buffer the caller must dispose.</remarks>
    public RefCountingMemory? GetLeafBlob(in Stem stem)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            // layers store an empty blob as the "stem deleted" marker, which must stop the walk
            if (snapshots[i].Content.LeafBlobs.TryGetValue(stem, out byte[]? blob)) return blob.Length == 0 ? null : RefCountingMemory.Wrapping(blob);
        }

        return reader.GetLeafBlob(stem);
    }

    public RefCountingMemory? GetTrieNode(in TrieNodeKey key)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            // a found null is a tombstone: the node was removed at this layer
            if (snapshots[i].Content.TrieNodes.TryGetValue(key, out byte[]? node)) return RefCountingMemory.WrappingOrNull(node);
        }

        return reader.GetTrieNode(key);
    }

    public bool TryLease() => TryAcquireLease();

    /// <remarks>Runs once the last lease drops, so no reader can be walking the chain or the reader.</remarks>
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
