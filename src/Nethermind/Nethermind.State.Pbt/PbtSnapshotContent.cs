// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Collections;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// One diff layer of state changes: the complete post-change leaf blobs and stem trie nodes produced
/// by the block's root computation.
/// </summary>
/// <remarks>
/// A blob is the whole of its stem, so a layer holding one has the final say on every account and slot
/// that stem carries — which is why the accounts and slots themselves are not stored a second time (see
/// <see cref="PbtLeafDecoder"/>). Both maps are concurrent because the root fold writes them from as
/// many threads as it runs across. Conventions: an empty blob = stem deleted; a null node = removed.
/// <para>
/// Pooled per <see cref="PbtResourcePool.Usage"/>, so an instance backs exactly one layer at a time
/// and must not be touched once returned.
/// </para>
/// </remarks>
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    public ConcurrentDictionary<Stem, byte[]> LeafBlobs { get; } = new();
    public ConcurrentDictionary<TrieNodeKey, byte[]?> TrieNodes { get; } = new();

    /// <remarks>
    /// The blob and node arrays are borrowed, not owned — compaction shares the very same arrays with
    /// the layers it merged — so releasing them here would hand live bytes to a second owner.
    /// <para>
    /// The lock-free clears are sound only at a pool-return boundary, where the layer's last lease has
    /// dropped and the fold that populates the maps has been joined.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        LeafBlobs.NoLockClear();
        TrieNodes.NoLockClear();
    }

    /// <remarks>No-op: the maps are managed and their arrays are borrowed. Present only so the pool can discard an instance it has no room to hold.</remarks>
    public void Dispose()
    {
    }
}
