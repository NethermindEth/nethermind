// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>Per-block scratch state that is never committed into a snapshot: the node groups staged for the shared trie cache.</summary>
public sealed class PbtTransientResource(int nodeGroupCapacity) : IDisposable, IResettable
{
    private IPbtResourcePool? _returnPool;
    private PbtResourcePool.Usage _returnUsage;

    /// <summary>Groups folded during this block, folded into the shared cache after the block commits.</summary>
    public PbtTrieNodeCache.ChildCache NodeGroups { get; } = new(nodeGroupCapacity);

    public void OnRented(IPbtResourcePool pool, PbtResourcePool.Usage usage)
    {
        _returnUsage = usage;
        Volatile.Write(ref _returnPool, pool);
    }

    /// <summary>Returns the resource to the pool it was rented from; the owner calls this exactly once per rental.</summary>
    /// <exception cref="InvalidOperationException">The resource is not rented, e.g. it was already returned.</exception>
    public void ReleaseLease()
    {
        // Claiming the pool guards against a double return putting one resource into two later rentals.
        IPbtResourcePool pool = Interlocked.Exchange(ref _returnPool, null)
            ?? throw new InvalidOperationException($"{nameof(PbtTransientResource)} released without an outstanding rental");
        pool.ReturnCachedResource(_returnUsage, this);
    }

    /// <summary>Clears staged groups, growing the cache if its capacity was exceeded.</summary>
    /// <remarks>Only the exclusive owner may reset the resource.</remarks>
    public void Reset() => NodeGroups.Reset();

    /// <summary>Releases staged groups when the exclusively owned resource is discarded, rather than returned to its pool.</summary>
    public void Dispose() => NodeGroups.Dispose();
}
