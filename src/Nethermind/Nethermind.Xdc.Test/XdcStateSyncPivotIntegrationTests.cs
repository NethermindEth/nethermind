// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Test.FastSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

/// <summary>
/// Verifies that state sync downloads all gap block states before finalizing at the real pivot.
/// </summary>
[TestFixture]
public class XdcStateSyncPivotIntegrationTests : StateSyncFeedTestsBase
{
    [Test]
    public async Task RunStateSyncRounds_WithMultiTargetPivot_SyncsAllTargetsBeforeFinalizing()
    {
        RemoteDbContext remote = new(_logManager);

        // Gap block 1 state: one account
        remote.StateTree.Set(TestItem.AddressA, Build.An.Account.WithBalance(1).TestObject);
        remote.StateTree.UpdateRootHash();
        remote.StateTree.Commit();
        Hash256 gapBlock1StateRoot = remote.StateTree.RootHash;

        // Gap block 2 state: two accounts
        remote.StateTree.Set(TestItem.AddressA, Build.An.Account.WithBalance(2).TestObject);
        remote.StateTree.Set(TestItem.AddressB, Build.An.Account.WithBalance(3).TestObject);
        remote.StateTree.UpdateRootHash();
        remote.StateTree.Commit();
        Hash256 gapBlock2StateRoot = remote.StateTree.RootHash;

        // Final pivot state: three accounts
        remote.StateTree.Set(TestItem.AddressA, Build.An.Account.WithBalance(4).TestObject);
        remote.StateTree.Set(TestItem.AddressB, Build.An.Account.WithBalance(5).TestObject);
        remote.StateTree.Set(TestItem.AddressC, Build.An.Account.WithBalance(6).TestObject);
        remote.StateTree.UpdateRootHash();
        remote.StateTree.Commit();
        Hash256 finalPivotStateRoot = remote.StateTree.RootHash;

        XdcBlockHeader xdcGapBlock1 = new XdcBlockHeaderBuilder()
            .WithNumber(50)
            .WithStateRoot(gapBlock1StateRoot)
            .TestObject;

        XdcBlockHeader xdcGapBlock2 = new XdcBlockHeaderBuilder()
            .WithNumber(75)
            .WithStateRoot(gapBlock2StateRoot)
            .TestObject;

        XdcBlockHeader xdcFinalPivot = new XdcBlockHeaderBuilder()
            .WithNumber(100)
            .WithStateRoot(finalPivotStateRoot)
            .TestObject;

        IXdcStateSyncSnapshotManager snapshotManager = Substitute.For<IXdcStateSyncSnapshotManager>();
        snapshotManager.GetGapBlocks(xdcFinalPivot).Returns([xdcGapBlock1, xdcGapBlock2]);

        await using IContainer container = PrepareDownloader(remote, configureBuilder: builder =>
        {
            builder.AddSingleton<IXdcStateSyncSnapshotManager>(snapshotManager);

            builder.AddSingleton<IStateSyncPivot>(ctx =>
            {
                IBlockTree blockTree = Substitute.For<IBlockTree>();
                blockTree.FindHeader(100L).Returns(xdcFinalPivot);

                ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
                syncConfig.PivotNumber.Returns(100L);

                IStateReader stateReader = ctx.Resolve<IStateReader>();
                return new XdcStateSyncPivot(blockTree, syncConfig, stateReader, snapshotManager);
            });
        });

        IStateSyncTestOperation local = container.Resolve<IStateSyncTestOperation>();
        SafeContext ctx = container.Resolve<SafeContext>();

        await ActivateAndWait(ctx);

        snapshotManager.Received(1).StoreSnapshot(xdcGapBlock1);
        snapshotManager.Received(1).StoreSnapshot(xdcGapBlock2);

        IStateReader stateReader = container.Resolve<IStateReader>();
        stateReader.HasStateForBlock(xdcGapBlock1).Should().BeTrue("gap block 1 state must be synced before finalizing");
        stateReader.HasStateForBlock(xdcGapBlock2).Should().BeTrue("gap block 2 state must be synced before finalizing");
        stateReader.HasStateForBlock(xdcFinalPivot).Should().BeTrue("final pivot state must be synced");
    }
}
