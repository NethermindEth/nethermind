// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Pbt;
using Nethermind.State.Flat.Persistence.BloomFilter;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>Per-block scratch state that is never committed into a snapshot: the node groups staged for the shared trie cache.</summary>
public sealed class PbtTransientResource(int nodeGroupCapacity) : IDisposable, IResettable
{
    private const long InitialReadNodeGroupCapacity = 16 * 1024;
    private const double ReadNodeGroupBitsPerKey = 14; // 14 is exactly 8 probes, which the SIMD instruction does.

    private IPbtResourcePool? _returnPool;
    private PbtResourcePool.Usage _returnUsage;

    /// <summary>Groups folded during this block, folded into the shared cache after the block commits.</summary>
    public PbtTrieNodeCache.ChildCache NodeGroups { get; } = new(nodeGroupCapacity);

    /// <summary>Groups the fold or the node group prefetch has read for this block.</summary>
    internal BloomFilter ReadNodeGroups { get; private set; } = new(InitialReadNodeGroupCapacity, ReadNodeGroupBitsPerKey);

    /// <summary>Records a read of <paramref name="groupKey"/> for this block.</summary>
    /// <returns>Whether no earlier read was recorded; a bloom false positive also returns false.</returns>
    internal bool TryClaimNodeGroupRead(PbtStorageNodePath groupKey)
    {
        ulong key = (uint)groupKey.GetHashCode();
        if (ReadNodeGroups.MightContain(key)) return false;
        ReadNodeGroups.Add(key);
        return true;
    }

    internal void OnRented(IPbtResourcePool pool, PbtResourcePool.Usage usage)
    {
        _returnUsage = usage;
        Volatile.Write(ref _returnPool, pool);
    }

    /// <summary>Returns the resource to the pool it was rented from; the owner calls this exactly once per rental.</summary>
    /// <exception cref="InvalidOperationException">The resource is not rented, e.g. it was already returned.</exception>
    internal void ReleaseLease()
    {
        // Claiming the pool guards against a double return putting one resource into two later rentals.
        IPbtResourcePool pool = Interlocked.Exchange(ref _returnPool, null)
            ?? throw new InvalidOperationException($"{nameof(PbtTransientResource)} released without an outstanding rental");
        pool.ReturnCachedResource(_returnUsage, this);
    }

    /// <summary>Clears staged groups and read node groups, growing each if its capacity was exceeded.</summary>
    /// <remarks>Only the exclusive owner may reset the resource.</remarks>
    public void Reset()
    {
        NodeGroups.Reset();
        if (ReadNodeGroups.Count > ReadNodeGroups.Capacity)
        {
            BloomFilter outgrown = ReadNodeGroups;
            ReadNodeGroups = new((long)BitOperations.RoundUpToPowerOf2((ulong)outgrown.Count), ReadNodeGroupBitsPerKey);
            outgrown.Dispose();
        }
        else if (ReadNodeGroups.Count != 0)
        {
            ReadNodeGroups.Clear();
        }
    }

    /// <summary>Releases staged groups and read node groups when the exclusively owned resource is discarded, rather than returned to its pool.</summary>
    public void Dispose()
    {
        NodeGroups.Dispose();
        ReadNodeGroups.Dispose();
    }
}
