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

    private static Hash256 CommitBlock(IWorldStateScopeProvider.IScope scope, ulong blockNumber, in UInt256 balance)
    {
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(Address, new Account(blockNumber, balance));
        }

        scope.UpdateRootHash();
        scope.Commit(blockNumber);
        return scope.RootHash;
    }

    private static BlockHeader Header(ulong number, Hash256 root) => Build.A.BlockHeader.WithNumber(number).WithStateRoot(root).TestObject;

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

        // reopen over the same db: the persisted head state is servable, pruned history is not
        await using (PbtTestContext reopened = new(db))
        {
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(3, root3)), Is.True);
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(1, root1)), Is.False);
            Assert.That(reopened.Manager.TryGatherBundle(new StateId(1, root1), PbtResourcePool.Usage.ReadOnlyProcessingEnv, isReadOnly: true), Is.Null);

            using IWorldStateScopeProvider.IScope scope = reopened.CreateScopeProvider().BeginScope(Header(3, root3), new LocalMetrics());
            Account? account = scope.Get(Address);
            Assert.That(account, Is.Not.Null);
            Assert.That(account!.Nonce, Is.EqualTo(3ul));
            Assert.That(account.Balance, Is.EqualTo((UInt256)300));
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

        // segments persisted up to the last CompactSize boundary at or below the finalized block
        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(new StateId(5, roots[5])));
        Assert.That(ctx.Repository.Count, Is.EqualTo(0));

        // the open scope keeps serving through its leased layers
        Assert.That(scope.Get(Address)!.Balance, Is.EqualTo((UInt256)500));
    }
}
