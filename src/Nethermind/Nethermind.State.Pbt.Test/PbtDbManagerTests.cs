// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtDbManagerTests
{
    private static readonly Address Address = TestItem.AddressA;

    /// <summary>The slot each block writes alongside the account, on a stem of its own rather than the account header's.</summary>
    private static readonly UInt256 Slot = 1000;

    private static Hash256 CommitBlock(IWorldStateScopeProvider.IScope scope, ulong blockNumber, in UInt256 balance)
    {
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(Address, new Account(blockNumber, balance));
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(Address, 1);
            storage.Set(Slot, [(byte)blockNumber]);
        }

        scope.UpdateRootHash();
        scope.Commit(blockNumber);
        return scope.RootHash;
    }

    private static BlockHeader Header(ulong number, Hash256 root) => Build.A.BlockHeader.WithNumber(number).WithStateRoot(root).TestObject;

    [Test]
    public async Task ReadOnlyBundle_IsSharedPerState_UntilTheBoundarySweepReleasesIt()
    {
        await using PbtTestContext ctx = new();
        Hash256 root1;
        using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
        {
            root1 = CommitBlock(scope, 1, 100);
        }

        StateId state = new(1, root1);
        IPbtDbManager manager = ctx.Manager;
        PbtReadOnlySnapshotBundle first = manager.GatherReadOnlyBundle(state);
        PbtReadOnlySnapshotBundle second = manager.GatherReadOnlyBundle(state);
        Assert.That(second, Is.SameAs(first), "one state, one shared view");

        // a gather still holding a lease keeps reading after the sweep drops the cached view
        ctx.Manager.FlushCache(default);
        Assert.That(first.GetAccount(Address)!.Balance, Is.EqualTo((UInt256)100));

        PbtReadOnlySnapshotBundle afterSweep = manager.GatherReadOnlyBundle(state);
        Assert.That(afterSweep, Is.Not.SameAs(first), "the swept view is not handed out again");

        first.Dispose();
        second.Dispose();
        afterSweep.Dispose();
    }

    [Test]
    public async Task CommitFlushReopen_ServesPersistedState_AndPrunesHistory()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        Hash256 root1;
        Hash256 root3;

        await using (PbtTestContext ctx = new(db))
        {
            using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
            {
                root1 = CommitBlock(scope, 1, 100);
                CommitBlock(scope, 2, 200);
                root3 = CommitBlock(scope, 3, 300);
            }

            ctx.Manager.FlushCache(default);
            using Persistence.IPbtPersistence.IReader reader = ctx.Persistence.CreateReader();
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(3, root3)));
        }

        await using (PbtTestContext reopened = new(db))
        {
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(3, root3)), Is.True);
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(1, root1)), Is.False);
            Assert.That(reopened.Manager.TryGatherReadOnlyBundle(new StateId(1, root1)), Is.Null);

            using IWorldStateScopeProvider.IScope scope = reopened.CreateScopeProvider().BeginScope(Header(3, root3), new LocalMetrics());
            Account? account = scope.Get(Address);
            Assert.That(account, Is.Not.Null);
            Assert.That(account!.Nonce, Is.EqualTo(3ul));
            Assert.That(account.Balance, Is.EqualTo((UInt256)300));
            Assert.That(scope.CreateStorageTree(Address).Get(Slot), Is.EqualTo((byte[])[3]), "and the slot decodes out of its own persisted blob");
        }
    }

    [Test]
    public async Task ForkCommitsFromSameParent_BothStatesReadable()
    {
        await using PbtTestContext ctx = new();
        Hash256 root1;
        using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
        {
            root1 = CommitBlock(scope, 1, 100);
        }

        Hash256 rootA;
        Hash256 rootB;
        using (IWorldStateScopeProvider.IScope scopeA = ctx.CreateScopeProvider().BeginScope(Header(1, root1), new LocalMetrics()))
        {
            rootA = CommitBlock(scopeA, 2, 222);
        }

        using (IWorldStateScopeProvider.IScope scopeB = ctx.CreateScopeProvider().BeginScope(Header(1, root1), new LocalMetrics()))
        {
            rootB = CommitBlock(scopeB, 2, 333);
        }

        Assert.That(rootA, Is.Not.EqualTo(rootB));
        Assert.That(ctx.Manager.HasStateForBlock(new StateId(2, rootA)), Is.True);
        Assert.That(ctx.Manager.HasStateForBlock(new StateId(2, rootB)), Is.True);

        using (IWorldStateScopeProvider.IScope onA = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(2, rootA), new LocalMetrics()))
        {
            Assert.That(onA.Get(Address)!.Balance, Is.EqualTo((UInt256)222));
        }

        using (IWorldStateScopeProvider.IScope onB = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(2, rootB), new LocalMetrics()))
        {
            Assert.That(onB.Get(Address)!.Balance, Is.EqualTo((UInt256)333));
        }

        using IWorldStateScopeProvider.IScope onParent = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(1, root1), new LocalMetrics());
        Assert.That(onParent.Get(Address)!.Balance, Is.EqualTo((UInt256)100));
    }

    [Test]
    public async Task FinalizedTrigger_PersistsCanonicalSegments_AndPrunesRepository()
    {
        await using PbtTestContext ctx = new(config: new PbtConfig { CompactSize = 2, MinReorgDepth = 1, MaxReorgDepth = 100 });

        Hash256[] roots = new Hash256[6];
        using IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        for (ulong number = 1; number <= 5; number++)
        {
            roots[number] = CommitBlock(scope, number, number * 100);
            ctx.FinalizedStateProvider.SetCanonicalRoot(number, roots[number]);
        }

        ctx.FinalizedStateProvider.FinalizedBlockNumber = 5;
        ctx.Coordinator.CheckPersistence();

        // Segments persisted up to the last schedule boundary at or below the finalized block. With
        // CompactSize 2 and no offset those are the even blocks, so block 5 is finalized but not yet
        // on a boundary and its layer stays in memory.
        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(new StateId(4, roots[4])));
        Assert.That(ctx.Repository.Count, Is.EqualTo(1));

        // the open scope keeps serving through its leased layers
        Assert.That(scope.Get(Address)!.Balance, Is.EqualTo((UInt256)500));
    }

    /// <summary>
    /// The whole point of the header-keyed state: a node restarting on a persisted database looks its
    /// state up by the root its header carries, and still folds the next block on the tree root it
    /// actually left off at.
    /// </summary>
    [Test]
    public async Task PersistedState_IsKeyedByTheHeaderRoot_WithTheTreeRootRecordedBesideIt()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtTestChildHeaders childHeaders = new();
        BlockHeader first;
        ValueHash256 treeRoot;

        await using (PbtTestContext ctx = new(db, childHeaders: childHeaders))
        {
            // block 0 has no header to echo, so its own tree root becomes the genesis header's claim
            BlockHeader genesis;
            using (IWorldStateScopeProvider.IScope genesisScope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
            {
                genesis = Header(0, CommitBlock(genesisScope, 0, 1));
            }

            first = childHeaders.Add(genesis, TestItem.KeccakA);
            using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(genesis, new LocalMetrics()))
            {
                Assert.That(CommitBlock(scope, 1, 100), Is.EqualTo(TestItem.KeccakA), "the block reports the root its header claims");
            }

            using (PbtReadOnlySnapshotBundle bundle = ((IPbtDbManager)ctx.Manager).GatherReadOnlyBundle(new StateId(first)))
            {
                treeRoot = bundle.TreeRoot;
            }

            Assert.That(treeRoot, Is.Not.EqualTo(TestItem.KeccakA.ValueHash256), "which is not the root the tree folded to");
            ctx.Manager.FlushCache(default);
        }

        await using (PbtTestContext reopened = new(db, childHeaders: childHeaders))
        {
            using (Persistence.IPbtPersistence.IReader reader = reopened.Persistence.CreateReader())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(new StateId(first)));
                Assert.That(reader.CurrentTreeRoot, Is.EqualTo(treeRoot));
            }

            BlockHeader second = childHeaders.Add(first, TestItem.KeccakB);
            using IWorldStateScopeProvider.IScope scope = reopened.CreateScopeProvider().BeginScope(first, new LocalMetrics());
            Assert.That(scope.Get(Address)!.Balance, Is.EqualTo((UInt256)100), "the persisted state is found by its header");
            Assert.That(CommitBlock(scope, 2, 200), Is.EqualTo(second.StateRoot), "and the branch carries on from it");
        }
    }
}
