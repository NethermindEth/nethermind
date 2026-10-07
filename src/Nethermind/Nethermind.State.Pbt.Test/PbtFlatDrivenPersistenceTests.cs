// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Mirror;
using Nethermind.State.Pbt.ScopeProvider;
using NSubstitute;
using NUnit.Framework;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;

namespace Nethermind.State.Pbt.Test;

public class PbtFlatDrivenPersistenceTests
{
    public enum FlatTarget
    {
        HeldState,
        /// <summary>A sync or import write that the mirrored PBT did not commit.</summary>
        UnknownState,
        PreGenesis,
        Sync
    }

    /// <summary>In mirror mode, flat writes exclusively drive PBT persistence.</summary>
    [Test]
    public async Task FlatWriteBatch_PersistsPbtOnlyForAStateItHolds([Values] FlatTarget target)
    {
        await using PbtTestContext ctx = NewContext();
        (Hash256 root1, Hash256 root2) = CommitTwoBlocks(ctx);

        // This satisfies the finalized trigger, so an ungated manager would persist.
        ctx.FinalizedStateProvider.SetCanonicalRoot(1, root1);
        ctx.FinalizedStateProvider.SetCanonicalRoot(2, root2);
        ctx.FinalizedStateProvider.FinalizedBlockNumber = 2;

        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(StateId.PreGenesis));

        FlatPersistence inner = Substitute.For<FlatPersistence>();
        FlatPersistence.IWriteBatch innerBatch = Substitute.For<FlatPersistence.IWriteBatch>();
        inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<WriteFlags>()).Returns(innerBatch);
        PbtFlatDrivenPersistence persistence = new(inner, new Lazy<PbtDbManager>(ctx.Manager), ctx.Persistence);

        StateId to = target switch
        {
            FlatTarget.HeldState => new StateId(2, root2),
            FlatTarget.UnknownState => new StateId(2, TestItem.KeccakA),
            FlatTarget.PreGenesis => StateId.PreGenesis,
            _ => StateId.Sync
        };

        FlatPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, to);

        Assert.That(batch, Is.SameAs(innerBatch), "the flat write batch must still be the inner one");
        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(),
            Is.EqualTo(target == FlatTarget.HeldState ? new StateId(2, root2) : StateId.PreGenesis));
    }

    /// <remarks>A compact size of one makes every canonical block persistable.</remarks>
    private static PbtTestContext NewContext() => new(config: new PbtConfig
    {
        MirrorFlat = true,
        CompactSize = 1,
        CompactionOffset = 0,
        MinReorgDepth = 0,
        MaxReorgDepth = 1000
    });

    private static (Hash256 Root1, Hash256 Root2) CommitTwoBlocks(PbtTestContext ctx)
    {
        PbtScopeProvider provider = ctx.CreateScopeProvider();

        Hash256 root1;
        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics()))
        {
            scope.Commit(0);
            Write(scope, TestItem.AddressA, 1, 100);
            scope.Commit(1);
            root1 = scope.RootHash;
        }

        BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(root1).TestObject;
        Hash256 root2;
        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(header1, new LocalMetrics()))
        {
            Write(scope, TestItem.AddressB, 2, 200);
            scope.Commit(2);
            root2 = scope.RootHash;
        }

        return (root1, root2);
    }

    private static void Write(IWorldStateScopeProvider.IScope scope, Address address, ulong nonce, UInt256 balance)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
        batch.Set(address, new Account(nonce, balance));
    }
}
