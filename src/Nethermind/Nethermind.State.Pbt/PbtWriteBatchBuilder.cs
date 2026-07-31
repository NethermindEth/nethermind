// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
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
    /// <summary>
    /// The stem count above which a shard's entry array is a large object, at the 48 bytes a stem keyed
    /// by a 32-byte value and mapped to a reference takes: past the 85,000-byte threshold, so it is never
    /// compacted and only a gen2 collection reclaims it.
    /// </summary>
    private const int LargeShardStems = 85_000 / 48;

    private readonly Partition[] _partitions = CreatePartitions();

    private IPbtResourcePool? _pool;
    private PbtResourcePool.Usage _usage;

    public bool HasDirtyStems => DirtyStemCount != 0;

    private int DirtyStemCount
    {
        get
        {
            int count = 0;
            foreach (Partition partition in _partitions)
            foreach (Shard shard in partition.Shards)
                count += shard.Stems.Count;
            return count;
        }
    }

    private sealed class Partition(PbtPartition kind)
    {
        internal readonly PbtPartition Kind = kind;
        internal readonly Shard[] Shards = CreateShards(PbtPartitions.StemShardCount(kind));
    }

    private sealed class Shard
    {
        internal readonly Lock Lock = new();
        internal Dictionary<Stem, IPbtStemChanges> Stems = [];
    }

    private static Partition[] CreatePartitions() =>
        [new(PbtPartition.Account), new(PbtPartition.Code), new(PbtPartition.Storage)];

    private static Shard[] CreateShards(int count)
    {
        Shard[] shards = new Shard[count];
        for (int i = 0; i < count; i++) shards[i] = new Shard();
        return shards;
    }

    private Shard ShardFor(in Stem stem)
    {
        PbtPartition partition = PbtPartitions.Of(stem);
        return _partitions[(int)partition].Shards[PbtPartitions.StemShard(partition, stem)];
    }

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

    /// <summary>Hands every dirtied stem to one fresh write batch per partition, emptying this builder.</summary>
    public PbtWriteBatchSet DrainToWriteBatches(PbtTiling tiling)
    {
        _ = tiling switch
        {
            PbtTiling.SixLevel or PbtTiling.EightLevel or PbtTiling.FourLevel or PbtTiling.FiveLevel => tiling,
            _ => throw new ArgumentOutOfRangeException(nameof(tiling), tiling, null),
        };

        PbtWriteBatch? account = null;
        PbtWriteBatch? code = null;
        PbtWriteBatch? storage = null;
        try
        {
            account = DrainPartition(_partitions[(int)PbtPartition.Account]);
            code = DrainPartition(_partitions[(int)PbtPartition.Code]);
            storage = DrainPartition(_partitions[(int)PbtPartition.Storage]);
            return new PbtWriteBatchSet(account, code, storage);
        }
        catch
        {
            account?.Dispose();
            code?.Dispose();
            storage?.Dispose();
            throw;
        }
        finally
        {
            ClearShards();
        }
    }

    private static PbtWriteBatch DrainPartition(Partition partition)
    {
        int count = 0;
        foreach (Shard shard in partition.Shards) count += shard.Stems.Count;

        PbtWriteBatch batch = new(count, buckets: null);
        foreach (Shard shard in partition.Shards)
        foreach ((Stem stem, IPbtStemChanges changes) in shard.Stems)
            batch.Add(stem, changes);
        return batch;
    }

    /// <summary>Returns every map still held — those a fold never claimed — and empties the builder.</summary>
    public void Reset()
    {
        try
        {
            foreach (Partition partition in _partitions)
            foreach (Shard shard in partition.Shards)
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
    /// would leave every shard holding a window's worth of large-object space that only the shard
    /// currently being filled has any use for.
    /// </para>
    /// </remarks>
    private void ClearShards()
    {
        foreach (Partition partition in _partitions)
        foreach (Shard shard in partition.Shards)
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
