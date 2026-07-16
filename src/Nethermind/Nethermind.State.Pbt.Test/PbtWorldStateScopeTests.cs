// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtWorldStateScopeTests
{
    // StaticPool<T>'s default cap; only a safety bound on the drain loop, not the drain itself
    private const int MaxPooledStemChanges = 4096;

    /// <summary>
    /// A scope abandoned with pending writes — an exception mid-block, or a branch dropped before its
    /// final fold — never reaches the drain in <c>BuildChanges</c>, so its rented maps must come back
    /// on disposal instead of being lost to the GC, which would silently starve the shared pool.
    /// </summary>
    [Test]
    public async Task Dispose_WithPendingWrites_ReturnsEveryRentedStemChanges()
    {
        DrainStemChangesPool();
        SingleStemChanges first = new();
        SingleStemChanges second = new();
        StaticPool<SingleStemChanges>.Return(first);
        StaticPool<SingleStemChanges>.Return(second);

        await using PbtTestContext ctx = new();
        PbtScopeProvider provider = ctx.CreateScopeProvider();

        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics()))
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            // two distinct stems — a header-region slot and a storage-zone slot — with one sub-index
            // each, so each rents exactly one map and neither promotes to a larger variant
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(TestItem.AddressC, 2);
            storageBatch.Set(3, [0x11]);
            storageBatch.Set(500, [0x11]);
        }

        // no UpdateRootHash and no Commit: disposal is the only thing that can return these
        object[] rented = [StaticPool<SingleStemChanges>.Rent(), StaticPool<SingleStemChanges>.Rent()];
        Assert.That(rented, Is.EquivalentTo(new object[] { first, second }));
    }

    /// <summary>Disposing twice must not return a map twice: two owners of one pooled map corrupt an unrelated stem.</summary>
    [Test]
    public async Task Dispose_CalledTwice_DoesNotReturnAStemChangesTwice()
    {
        DrainStemChangesPool();
        SingleStemChanges seeded = new();
        StaticPool<SingleStemChanges>.Return(seeded);

        await using PbtTestContext ctx = new();
        PbtScopeProvider provider = ctx.CreateScopeProvider();

        IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics());
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(0))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(TestItem.AddressC, 1);
            storageBatch.Set(500, [0x11]);
        }

        scope.Dispose();
        scope.Dispose();

        Assert.That(StaticPool<SingleStemChanges>.Rent(), Is.SameAs(seeded), "the one rented map must come back exactly once");
        Assert.That(StaticPool<SingleStemChanges>.Rent(), Is.Not.SameAs(seeded), "a second dispose must not re-return it");
    }

    /// <summary>
    /// Empties the process-wide pool so the maps it hands out next are the ones the test seeded.
    /// </summary>
    /// <remarks>
    /// The pool is FIFO, so whatever an earlier test left queued comes out ahead of a freshly
    /// returned sentinel: renting until the sentinel reappears drains exactly the backlog, whatever
    /// its size, without depending on the pool's cap.
    /// </remarks>
    private static void DrainStemChangesPool()
    {
        SingleStemChanges sentinel = new();
        StaticPool<SingleStemChanges>.Return(sentinel);

        for (int i = 0; i <= MaxPooledStemChanges; i++)
        {
            if (ReferenceEquals(StaticPool<SingleStemChanges>.Rent(), sentinel)) return;
        }

        Assert.Fail("the stem-change pool never returned the sentinel, so it could not be drained");
    }
}
