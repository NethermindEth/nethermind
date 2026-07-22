// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// One diff layer of state changes: flat entries keyed by raw address/slot, plus the complete
/// post-change leaf blobs and stem trie nodes produced by the block's root computation.
/// </summary>
/// <remarks>
/// Every map is concurrent: the account and slot ones because block processing populates them from
/// parallel write batches, the blob and node ones because the root fold writes them from as many
/// threads as it runs across. Conventions: a null account = deleted; a present slot entry = written
/// in this layer (its value may be zero); an empty blob = stem deleted; a null node = removed.
/// <para>
/// Pooled per <see cref="PbtResourcePool.Usage"/>, so an instance backs exactly one layer at a time
/// and must not be touched once returned.
/// </para>
/// </remarks>
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    public ConcurrentDictionary<AddressAsKey, Account?> Accounts { get; } = new();
    public ConcurrentDictionary<(AddressAsKey Address, UInt256 Slot), EvmWord> Slots { get; } = new();
    public ConcurrentDictionary<AddressAsKey, bool> SelfDestructs { get; } = new();
    public ConcurrentDictionary<Stem, byte[]> LeafBlobs { get; } = new();
    public ConcurrentDictionary<TrieNodeKey, byte[]?> TrieNodes { get; } = new();

    /// <remarks>
    /// The blob and node arrays are borrowed, not owned — compaction shares the very same arrays with
    /// the layers it merged — so releasing them here would hand live bytes to a second owner.
    /// <para>
    /// The lock-free clears are sound only at a pool-return boundary, where the layer's last lease has
    /// dropped, and the storage batches and the fold that populate the maps have both been joined.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        Accounts.NoLockClear();
        Slots.NoLockClear();
        SelfDestructs.NoLockClear();
        LeafBlobs.NoLockClear();
        TrieNodes.NoLockClear();
    }

    /// <remarks>No-op: the maps are managed and their arrays are borrowed. Present only so the pool can discard an instance it has no room to hold.</remarks>
    public void Dispose()
    {
    }
}
