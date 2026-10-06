// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>Per-block scratch state that is never committed into a snapshot: the node groups staged for the shared trie cache.</summary>
public sealed class PbtTransientResource(int nodeGroupCapacity = 1024) : IDisposable, IResettable
{
    private long _leases = RefCountingLease.Single;
    private IPbtResourcePool? _returnPool;
    private PbtResourcePool.Usage _returnUsage;

    /// <summary>Groups folded during this block, folded into the shared cache after the block commits.</summary>
    public PbtTrieNodeCache.ChildCache NodeGroups { get; } = new(nodeGroupCapacity);

    internal void OnRented(IPbtResourcePool pool, PbtResourcePool.Usage usage)
    {
        _returnPool = pool;
        _returnUsage = usage;
        Volatile.Write(ref _leases, RefCountingLease.Single);
    }

    internal bool TryAcquireLease() => RefCountingLease.TryAcquire(ref _leases);

    /// <summary>Spins until only the owner lease remains.</summary>
    internal void WaitForExclusiveLease()
    {
        SpinWait spinWait = default;
        while (Volatile.Read(ref _leases) != RefCountingLease.Single) spinWait.SpinOnce();
    }

    internal void ReleaseLease()
    {
        if (RefCountingLease.ReleaseOnce(ref _leases))
        {
            if (_returnPool is null)
                throw new InvalidOperationException($"{nameof(PbtTransientResource)} final lease released without a registered return pool");
            _returnPool.ReturnCachedResource(_returnUsage, this);
        }
    }

    /// <summary>Clears staged groups, growing the cache if its capacity was exceeded.</summary>
    /// <remarks>Only the exclusive owner may reset the resource after all query leases have drained.</remarks>
    public void Reset() => NodeGroups.Reset();

    /// <summary>Releases staged groups when the exclusively owned resource is discarded, rather than returned to its pool.</summary>
    public void Dispose() => NodeGroups.Dispose();
}
