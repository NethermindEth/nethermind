// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.Pbt.Mirror;
using Nethermind.State.Pbt.ScopeProvider;
using NSubstitute;
using NUnit.Framework;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Test;

public class PbtFlatDrivenPersistenceTests
{
    public enum NoOpCase
    {
        /// <summary>A state the mirrored pbt never committed - a sync or import write.</summary>
        UnknownState,
        PreGenesis,
        Sync
    }

    /// <summary>
    /// The flat backend is the only clock in mirror mode: pbt's own triggers must stay idle even where
    /// they would otherwise fire, and the flat write batch must move pbt to exactly the same state.
    /// </summary>
    [Test]
    public async Task FlatWriteBatch_DrivesPbtPersistence_WhileItsOwnTriggersStayIdle()
    {
        await using PbtTestContext ctx = NewContext();
        (Hash256 root1, Hash256 root2) = CommitTwoBlocks(ctx);

        // everything the finalized trigger asks for, so an ungated coordinator would persist here
        ctx.FinalizedStateProvider.SetCanonicalRoot(1, root1);
        ctx.FinalizedStateProvider.SetCanonicalRoot(2, root2);
        ctx.FinalizedStateProvider.FinalizedBlockNumber = 2;

        Assert.That(ctx.Coordinator.CheckPersistence(), Is.False);
        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(StateId.PreGenesis));

        FlatPersistence inner = Substitute.For<FlatPersistence>();
        FlatPersistence.IWriteBatch innerBatch = Substitute.For<FlatPersistence.IWriteBatch>();
        inner.CreateWriteBatch(Arg.Any<FlatStateId>(), Arg.Any<FlatStateId>(), Arg.Any<WriteFlags>()).Returns(innerBatch);
        PbtFlatDrivenPersistence persistence = new(inner, ctx.Manager, ctx.Persistence);

        FlatPersistence.IWriteBatch batch = persistence.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(2, root2));

        Assert.That(batch, Is.SameAs(innerBatch), "the flat write batch must still be the inner one");
        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(new StateId(2, root2)));
    }

    [Test]
    public async Task FlatWriteBatch_ForAStatePbtDoesNotHold_LeavesItWhereItWas([Values] NoOpCase noOpCase)
    {
        await using PbtTestContext ctx = NewContext();
        CommitTwoBlocks(ctx);

        FlatStateId to = noOpCase switch
        {
            NoOpCase.UnknownState => new FlatStateId(2, TestItem.KeccakA),
            NoOpCase.PreGenesis => FlatStateId.PreGenesis,
            _ => FlatStateId.Sync
        };

        FlatPersistence inner = Substitute.For<FlatPersistence>();
        PbtFlatDrivenPersistence persistence = new(inner, ctx.Manager, ctx.Persistence);

        persistence.CreateWriteBatch(FlatStateId.PreGenesis, to);

        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(StateId.PreGenesis));
    }

    /// <remarks>
    /// A compact size of one with no reorg floor puts a persistable boundary at every block, so the
    /// finalized trigger is armed as soon as a canonical root is published for one.
    /// </remarks>
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
