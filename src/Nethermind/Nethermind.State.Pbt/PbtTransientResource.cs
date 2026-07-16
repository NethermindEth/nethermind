// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// The large per-block state a scope accumulates but never commits: the stem leaves dirtied since
/// the last root update, waiting to be folded into the tree. Pooled purely for performance.
/// </summary>
/// <remarks>
/// Owned by one scope for its whole life. Unlike the layer content it never crosses the commit
/// boundary — <see cref="DrainToWriteBatch"/> empties it at every fold and the scope returns it on
/// disposal — so it belongs to the scope rather than to the bundle, which also keeps a bundle built
/// only to read (an <c>eth_call</c> override reader) from renting one at all.
/// <para>
/// The outer map is concurrent because the parallel storage batches add stems from several threads,
/// but each stem is address-derived and written by a single worker, so its per-stem change map is
/// single-writer.
/// </para>
/// </remarks>
public sealed class PbtTransientResource : IDisposable, IResettable
{
    private readonly ConcurrentDictionary<Stem, IPbtStemChanges> _dirtyStems = new();

    public bool HasDirtyStems => !_dirtyStems.IsEmpty;

    /// <summary>Folds one leaf write into its stem's pooled change map.</summary>
    /// <remarks>
    /// <see cref="IPbtStemChanges.Set"/> may promote the map to a larger variant and return the old
    /// one to the pool, so its result must always be stored back; routing single-leaf writes through
    /// here makes forgetting that unrepresentable. Safe only while at most one thread writes a given
    /// stem: <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/> may
    /// run the factory and discard its result, which would leak the rented map. That holds because
    /// <c>PersistentStorageProvider</c> partitions its parallel batches by address, and code-overflow
    /// stems are reached only from the single-threaded account flush.
    /// </remarks>
    public void SetLeaf(in Stem stem, byte subIndex, in ValueHash256 value)
    {
        IPbtStemChanges changes = _dirtyStems.GetOrAdd(stem, static _ => PbtStemChanges.Rent());
        _dirtyStems[stem] = changes.Set(subIndex, value);
    }

    /// <summary>Takes the change map for <paramref name="stem"/>, renting one if the stem is not dirty yet.</summary>
    /// <remarks>
    /// The batched counterpart of <see cref="SetLeaf"/>, for a caller folding several leaves onto one
    /// stem: it accumulates on the returned reference and must hand the result back through
    /// <see cref="StoreStemChanges"/> — in a <c>finally</c> — because a promotion returns the old map
    /// to the pool, and leaving it here would give one pooled map two owners. Prefer
    /// <see cref="SetLeaf"/> wherever a single leaf is written; it cannot be misused this way.
    /// </remarks>
    public IPbtStemChanges RentStemChanges(in Stem stem) => _dirtyStems.GetOrAdd(stem, static _ => PbtStemChanges.Rent());

    /// <inheritdoc cref="RentStemChanges"/>
    public void StoreStemChanges(in Stem stem, IPbtStemChanges changes) => _dirtyStems[stem] = changes;

    /// <summary>Hands every dirtied stem to a fresh write batch, emptying this resource.</summary>
    /// <remarks>
    /// Ownership of the maps passes to the batch, which returns them to the pool when disposed, so
    /// the clear is adjacent to the drain and unconditional: leaving a transferred map here as well
    /// would give one pooled map two owners, and a later block's writes would silently surface under
    /// an unrelated stem. If a hand-off throws mid-drain the untransferred maps are dropped to the
    /// GC instead — a lost map costs an allocation, a doubly-returned one costs correctness.
    /// </remarks>
    public PbtWriteBatch DrainToWriteBatch()
    {
        PbtWriteBatch batch = new(estimatedStems: _dirtyStems.Count);
        try
        {
            foreach ((Stem stem, IPbtStemChanges leaves) in _dirtyStems)
            {
                batch.Add(stem, leaves);
            }
        }
        finally
        {
            _dirtyStems.Clear();
        }

        return batch;
    }

    /// <summary>Returns every map still held — those a fold never claimed — and empties the resource.</summary>
    /// <remarks>
    /// Only maps <see cref="DrainToWriteBatch"/> did not already transfer are here, which is what
    /// makes this safe to call after any number of folds. The lock-free clear is sound because a
    /// scope returns its resource only once its parallel storage batches have been joined.
    /// </remarks>
    public void Reset()
    {
        try
        {
            foreach ((_, IPbtStemChanges changes) in _dirtyStems)
            {
                PbtStemChanges.Return(changes);
            }
        }
        finally
        {
            _dirtyStems.NoLockClear();
        }
    }

    /// <summary>Nothing to release beyond <see cref="Reset"/>, which the pool has already run.</summary>
    /// <remarks>Present only so the pool can discard an instance it has no room to hold.</remarks>
    public void Dispose()
    {
    }
}
