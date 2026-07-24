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
/// the last root update, waiting to be folded into the tree.
/// </summary>
/// <remarks>
/// Owned by one scope for its whole life; it never crosses the commit boundary, so it belongs to the
/// scope rather than to the bundle.
/// <para>
/// The stem map is sharded under one lock per shard because the parallel storage batches add stems from
/// several threads. Both writing methods hold their shard's lock across the change map's own mutation,
/// so a stem needs no single-writer guarantee of its own.
/// </para>
/// </remarks>
public sealed class PbtWriteBatchBuilder : IDisposable, IResettable
{
    private const int ShardCount = 256;

    /// <summary>
    /// The stem count above which a shard's entry array is a large object, at the 48 bytes a stem keyed
    /// by a 32-byte value and mapped to a reference takes: past the 85,000-byte threshold, so it is never
    /// compacted and only a gen2 collection reclaims it.
    /// </summary>
    private const int LargeShardStems = 85_000 / 48;

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
        internal Dictionary<Stem, IPbtStemChanges> Stems = [];
    }

    private static Shard[] CreateShards()
    {
        Shard[] shards = new Shard[ShardCount];
        for (int i = 0; i < ShardCount; i++) shards[i] = new Shard();
        return shards;
    }

    /// <remarks>
    /// EIP-8297 puts the 4-bit zone in the first byte, so account stems land in shards 0x00-0x0F and code
    /// stems in 0x10-0x1F, while storage — the only zone written in parallel, and the one that dominates
    /// a block's stems — spreads over 0x80-0xFF on the address-hash bits that follow its high bit.
    /// </remarks>
    private Shard ShardFor(in Stem stem) => _shards[stem.Bytes[0]];

    /// <summary>Records the pool <see cref="Dispose"/> returns this builder to.</summary>
    internal void RentedFrom(IPbtResourcePool pool, PbtResourcePool.Usage usage)
    {
        _pool = pool;
        _usage = usage;
    }

    /// <summary>Folds one leaf write into its stem's pooled change map.</summary>
    /// <remarks>
    /// <see cref="IPbtStemChanges.Set"/> may promote the map to a larger variant and return the old one
    /// to the pool, so its result must always be stored back. The shard's lock is held across the
    /// promotion, so the map a concurrent writer of the same stem finds is never one already returned
    /// to the pool.
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
    /// <remarks><paramref name="values"/> must fit the stem from <paramref name="startSubIndex"/>.</remarks>
    public void SetLeafRange(in Stem stem, byte startSubIndex, ReadOnlySpan<byte> values)
    {
        Shard shard = ShardFor(stem);
        lock (shard.Lock)
        {
            ref IPbtStemChanges? changes = ref CollectionsMarshal.GetValueRefOrAddDefault(shard.Stems, stem, out bool exists);
            changes = (exists ? changes! : PbtStemChanges.Rent()).SetRange(startSubIndex, values);
        }
    }

    /// <summary>
    /// Folds a whole stem's leaves — <paramref name="subIndices"/> strictly ascending, though not
    /// necessarily consecutive, and as long as <paramref name="values"/> — into its pooled change map.
    /// </summary>
    /// <remarks>
    /// What a <see cref="SetLeaf"/> per leaf costs and this does not: a map holds one tier's worth of
    /// leaves and promotes when it fills, so a stem of many leaves rents and returns every tier below its
    /// own on the way up. Knowing the whole stem up front rents the tier that holds it, once.
    /// <para>
    /// Only the first writer of a stem takes that path; leaves for a stem already dirtied fold in one at
    /// a time, exactly as <see cref="SetLeaf"/> would.
    /// </para>
    /// </remarks>
    public void SetLeaves(in Stem stem, ReadOnlySpan<byte> subIndices, ReadOnlySpan<ValueHash256> values)
    {
        // before the map is reached: an empty group must not leave a stem dirtied with no leaves
        if (subIndices.IsEmpty) return;

        Shard shard = ShardFor(stem);
        lock (shard.Lock)
        {
            ref IPbtStemChanges? changes = ref CollectionsMarshal.GetValueRefOrAddDefault(shard.Stems, stem, out bool exists);
            if (!exists && subIndices.Length > 1)
            {
                changes = PbtStemChanges.RentSeeded(subIndices.Length, subIndices, values);
                return;
            }

            IPbtStemChanges map = exists ? changes! : PbtStemChanges.Rent();
            for (int i = 0; i < subIndices.Length; i++) map = map.Set(subIndices[i], values[i]);
            changes = map;
        }
    }

    /// <summary>Hands every dirtied stem to a fresh write batch, emptying this builder.</summary>
    /// <remarks>
    /// Ownership of the maps passes to the batch, which returns them to the pool when disposed, so the
    /// clear is unconditional: leaving a transferred map here as well would give one pooled map two
    /// owners, and a later block's writes would silently surface under an unrelated stem. If a hand-off
    /// throws mid-drain the untransferred maps are dropped to the GC instead — a lost map costs an
    /// allocation, a doubly-returned one costs correctness.
    /// <para>
    /// Draining the shards in ascending order hands the batch its entries already bucketed for as many
    /// of the tree's topmost levels as the shard key — the stem's first byte — spans, letting
    /// <see cref="TrieUpdater"/> skip re-deriving those bounds. That is the first two levels of a
    /// four-level tiling, whose slots are the byte's two nibbles, but only the first of a six-level
    /// one, whose second level starts inside the next byte; the rest that tiling partitions for itself.
    /// </para>
    /// </remarks>
    /// <param name="tiling">The tiling the batch will be applied in, whose levels the table is bucketed for.</param>
    public PbtWriteBatch DrainToWriteBatch(PbtTiling tiling) => tiling switch
    {
        PbtTiling.ClusteredFourLevel => DrainToWriteBatch<PbtClusteredTileLayout>(),
        PbtTiling.SixLevel => DrainToWriteBatch<PbtSixLevelTileLayout>(),
        PbtTiling.EightLevel => DrainToWriteBatch<PbtEightLevelTileLayout>(),
        _ => throw new ArgumentOutOfRangeException(nameof(tiling), tiling, null),
    };

    private PbtWriteBatch DrainToWriteBatch<TLayout>() where TLayout : IPbtTileLayout
    {
        // The coarse level sits last so a descent finds its own level at the table's end whatever the
        // level count, and slot h's child level at h * LevelStride. A group's ends count from the start
        // of its own coarse slot rather than of the batch, which is what lets the descent below use them
        // as its bounds unchanged; the coarse level, whose range is the whole batch, is the same thing
        // at depth 0.
        int stride = PbtWriteBatch.LevelStride<TLayout>();
        int slots = TLayout.BoundarySlots;
        bool nested = ShardCount >= slots * slots;
        int tableLength = nested ? slots * stride + stride : stride;
        ArrayPoolList<int> buckets = new(tableLength, tableLength);
        PbtWriteBatch batch = new(estimatedStems: DirtyStemCount, buckets);
        try
        {
            Span<int> table = buckets.AsSpan();
            Span<int> coarse = table[(tableLength - stride)..];
            coarse[0] = 0;
            int coarseStart = 0;
            coarse[PbtWriteBatch.TouchedMaskIndex<TLayout>()..].Clear();

            // The shards a coarse slot covers: the first byte's high bits are the slot, and what is left
            // of the byte splits it further — into the next level's slots where the tiling is narrow
            // enough for a whole level to fit, and into nothing the descent can use where it is not.
            int shardsPerSlot = ShardCount / slots;
            for (int slot = 0; slot < slots; slot++)
            {
                Span<int> fine = nested ? table.Slice(slot * stride, stride) : default;
                if (nested) fine[0] = 0;
                if (nested) fine[PbtWriteBatch.TouchedMaskIndex<TLayout>()..].Clear();
                for (int shard = 0; shard < shardsPerSlot; shard++)
                {
                    int shardStart = batch.Count;
                    foreach ((Stem stem, IPbtStemChanges leaves) in _shards[slot * shardsPerSlot + shard].Stems)
                    {
                        batch.Add(stem, leaves);
                    }

                    if (!nested) continue;

                    if (batch.Count != shardStart) PbtWriteBatch.SetTouched<TLayout>(fine, shard);
                    fine[shard + 1] = batch.Count - coarseStart;
                }

                if (batch.Count != coarseStart) PbtWriteBatch.SetTouched<TLayout>(coarse, slot);
                coarse[slot + 1] = batch.Count;
                coarseStart = batch.Count;
            }
        }
        finally
        {
            ClearShards();
        }

        return batch;
    }

    /// <summary>Returns every map still held — those a fold never claimed — and empties the builder.</summary>
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
    /// scope's parallel storage batches have been joined.
    /// <para>
    /// A shard that grew a large-object entry array is replaced rather than cleared, a clear keeping its
    /// capacity. Otherwise a bulk load — whose ascending stems make one shard at a time the hot one —
    /// would leave all 256 of them holding a window's worth of large-object space that only the shard
    /// currently being filled has any use for.
    /// </para>
    /// </remarks>
    private void ClearShards()
    {
        foreach (Shard shard in _shards)
        {
            if (shard.Stems.Count > LargeShardStems) shard.Stems = [];
            else shard.Stems.Clear();
        }
    }

    /// <summary>Returns this builder to the pool it was rented from, which resets it on the way in.</summary>
    /// <remarks>
    /// Detaching before returning is what keeps this from recursing: the pool discards a builder it has
    /// no room to hold by disposing it, and that lands back here with nothing left to return.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref _pool, null)?.ReturnWriteBatchBuilder(_usage, this);
}
