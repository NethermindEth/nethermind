// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
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
/// boundary — <see cref="DrainToWriteBatch"/> empties it at every fold and <see cref="Dispose"/>
/// returns it — so it belongs to the scope rather than to the bundle, which also keeps a bundle built
/// only to read (an <c>eth_call</c> override reader) from renting one at all.
/// <para>
/// The stem map is sharded under one lock per shard because the parallel storage batches add stems from
/// several threads. Both writing methods hold their shard's lock across the change map's own mutation,
/// so a stem needs no single-writer guarantee of its own.
/// </para>
/// </remarks>
public sealed class PbtWriteBatchBuilder : IDisposable, IResettable
{
    /// <summary>One shard per value of the shard key, which is a stem's first byte.</summary>
    private const int ShardCount = 256;

    private readonly Shard[] _shards = CreateShards();

    private IPbtResourcePool? _pool;
    private PbtResourcePool.Usage _usage;

    public bool HasDirtyStems => DirtyStemCount != 0;

    private int DirtyStemCount
    {
        get
        {
            int count = 0;
            foreach (Shard shard in _shards) count += shard.Stems.Count;
            return count;
        }
    }

    private sealed class Shard
    {
        internal readonly Lock Lock = new();
        internal readonly Dictionary<Stem, IPbtStemChanges> Stems = [];
    }

    private static Shard[] CreateShards()
    {
        Shard[] shards = new Shard[ShardCount];
        for (int i = 0; i < ShardCount; i++) shards[i] = new Shard();
        return shards;
    }

    /// <summary>The shard <paramref name="stem"/>'s changes live in.</summary>
    /// <remarks>
    /// EIP-8297 puts the 4-bit zone in the first byte, so account stems land in shards 0x00-0x0F and code
    /// stems in 0x10-0x1F, while storage — the only zone written in parallel, and the one that dominates
    /// a block's stems — spreads over 0x80-0xFF on the address-hash bits that follow its high bit.
    /// </remarks>
    private Shard ShardFor(in Stem stem) => _shards[stem.Bytes[0]];

    /// <summary>Records the pool <see cref="Dispose"/> returns this builder to.</summary>
    /// <remarks>Called on every rent, since a returned builder has been detached.</remarks>
    internal void RentedFrom(IPbtResourcePool pool, PbtResourcePool.Usage usage)
    {
        _pool = pool;
        _usage = usage;
    }

    /// <summary>Folds one leaf write into its stem's pooled change map.</summary>
    /// <remarks>
    /// <see cref="IPbtStemChanges.Set"/> may promote the map to a larger variant and return the old one
    /// to the pool, so its result must always be stored back; routing every leaf write through here
    /// makes forgetting that unrepresentable. The shard's lock is held across the promotion, so the
    /// map a concurrent writer of the same stem finds is never one already returned to the pool.
    /// </remarks>
    public void SetLeaf(in Stem stem, byte subIndex, in ValueHash256 value)
    {
        Shard shard = ShardFor(stem);
        lock (shard.Lock)
        {
            ref IPbtStemChanges? changes = ref CollectionsMarshal.GetValueRefOrAddDefault(shard.Stems, stem, out bool exists);
            changes = (exists ? changes! : PbtStemChanges.Rent()).Set(subIndex, value);
        }
    }

    /// <summary>
    /// Folds a run of leaves — <paramref name="values"/> split into consecutive
    /// <see cref="ValueHash256.MemorySize"/>-byte values — onto consecutive sub-indices of one stem,
    /// starting at <paramref name="startSubIndex"/>.
    /// </summary>
    /// <remarks>
    /// The batched <see cref="SetLeaf"/>, taking the change map and the shard's lock once per run rather
    /// than once per leaf. <paramref name="values"/> must fit the stem from
    /// <paramref name="startSubIndex"/>.
    /// </remarks>
    public void SetLeafRange(in Stem stem, byte startSubIndex, ReadOnlySpan<byte> values)
    {
        Shard shard = ShardFor(stem);
        lock (shard.Lock)
        {
            ref IPbtStemChanges? changes = ref CollectionsMarshal.GetValueRefOrAddDefault(shard.Stems, stem, out bool exists);
            changes = (exists ? changes! : PbtStemChanges.Rent()).SetRange(startSubIndex, values);
        }
    }

    /// <summary>Hands every dirtied stem to a fresh write batch, emptying this builder.</summary>
    /// <remarks>
    /// Ownership of the maps passes to the batch, which returns them to the pool when disposed, so
    /// the clear is adjacent to the drain and unconditional: leaving a transferred map here as well
    /// would give one pooled map two owners, and a later block's writes would silently surface under
    /// an unrelated stem. If a hand-off throws mid-drain the untransferred maps are dropped to the
    /// GC instead — a lost map costs an allocation, a doubly-returned one costs correctness.
    /// <para>
    /// The shard key being the stem's first byte makes it the two nibbles the tree's first two levels
    /// partition on, so draining the shards in order hands the batch its entries already bucketed for
    /// those levels — the two that touch every entry. Recording the bounds here, which costs only the
    /// counts the walk passes anyway, is what lets <see cref="TrieUpdater"/> skip re-deriving them.
    /// The nesting tracks each nibble's start so its group's ends can be local to it, as the table's
    /// layout requires.
    /// </para>
    /// </remarks>
    public PbtWriteBatch DrainToWriteBatch()
    {
        ArrayPoolList<int> buckets = new(PbtWriteBatch.BucketTableLength, PbtWriteBatch.BucketTableLength);
        PbtWriteBatch batch = new(estimatedStems: DirtyStemCount, buckets);
        try
        {
            Span<int> table = buckets.AsSpan();
            Span<int> nibbles = table[PbtWriteBatch.ByteLevelLength..];
            nibbles[0] = 0;
            int nibbleStart = 0;
            for (int nibble = 0; nibble < PbtTrieNodeGroup.BoundarySlots; nibble++)
            {
                Span<int> group = table.Slice(nibble * PbtWriteBatch.LevelStride, PbtWriteBatch.LevelStride);
                group[0] = 0;
                for (int low = 0; low < PbtTrieNodeGroup.BoundarySlots; low++)
                {
                    foreach ((Stem stem, IPbtStemChanges leaves) in _shards[(nibble << 4) | low].Stems)
                    {
                        batch.Add(stem, leaves);
                    }

                    group[low + 1] = batch.Count - nibbleStart;
                }

                nibbles[nibble + 1] = batch.Count;
                nibbleStart = batch.Count;
            }
        }
        finally
        {
            ClearShards();
        }

        return batch;
    }

    /// <summary>Returns every map still held — those a fold never claimed — and empties the builder.</summary>
    /// <remarks>
    /// Only maps <see cref="DrainToWriteBatch"/> did not already transfer are here, which is what
    /// makes this safe to call after any number of folds.
    /// </remarks>
    public void Reset()
    {
        try
        {
            foreach (Shard shard in _shards)
            {
                foreach ((_, IPbtStemChanges changes) in shard.Stems)
                {
                    PbtStemChanges.Return(changes);
                }
            }
        }
        finally
        {
            ClearShards();
        }
    }

    /// <remarks>
    /// Lock-free, like the enumeration each caller does first: a fold and a reset both run only once the
    /// scope's parallel storage batches have been joined. Clearing keeps each shard's capacity, which is
    /// most of what makes a pooled builder worth pooling.
    /// </remarks>
    private void ClearShards()
    {
        foreach (Shard shard in _shards) shard.Stems.Clear();
    }

    /// <summary>Returns this builder to the pool it was rented from, which resets it on the way in.</summary>
    /// <remarks>
    /// Detaching before returning is what keeps this from recursing: the pool discards a builder it has
    /// no room to hold by disposing it, and that lands back here with nothing left to return. It also
    /// makes a double dispose — or a dispose of a builder that was never rented — a no-op.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref _pool, null)?.ReturnWriteBatchBuilder(_usage, this);
}
