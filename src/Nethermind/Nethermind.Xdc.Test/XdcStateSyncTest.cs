// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
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
public class XdcStateSyncTest : StateSyncFeedTestsBase
{
    [TestCase(0)]
    [TestCase(2)]
    public async Task RunStateSyncRounds_WithMultiTargetPivot_SyncsAllTargetsBeforeFinalizing(int gapBlockCount)
    {
        RemoteDbContext remote = new(_logManager);

        XdcBlockHeader[] gapBlocks = new XdcBlockHeader[gapBlockCount];
        for (int i = 0; i < gapBlockCount; i++)
        {
            remote.StateTree.Set(TestItem.Addresses[i], Build.An.Account.WithBalance((uint)(i + 1)).TestObject);
            remote.StateTree.UpdateRootHash();
            remote.StateTree.Commit();

            gapBlocks[i] = new XdcBlockHeaderBuilder()
                .WithNumber((ulong)(i + 1) * 25)
                .WithStateRoot(remote.StateTree.RootHash)
                .TestObject;
        }

        // Final pivot state
        remote.StateTree.Set(TestItem.AddressC, Build.An.Account.WithBalance(99).TestObject);
        remote.StateTree.UpdateRootHash();
        remote.StateTree.Commit();

        XdcBlockHeader xdcFinalPivot = new XdcBlockHeaderBuilder()
            .WithNumber((ulong)(gapBlockCount + 1) * 25)
            .WithStateRoot(remote.StateTree.RootHash)
            .TestObject;

        IXdcStateSyncSnapshotManager snapshotManager = Substitute.For<IXdcStateSyncSnapshotManager>();
        snapshotManager.GetGapBlocks(xdcFinalPivot).Returns(gapBlocks);

        await using IContainer container = PrepareDownloader(remote, configureBuilder: builder =>
        {
            builder.AddSingleton<IXdcStateSyncSnapshotManager>(snapshotManager);

            builder.AddSingleton<IStateSyncPivot>(context =>
            {
                IBlockTree blockTree = Substitute.For<IBlockTree>();
                blockTree.FindHeader(xdcFinalPivot.Number).Returns(xdcFinalPivot);

                ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
                syncConfig.PivotNumber.Returns(xdcFinalPivot.Number);

                IStateReader stateReader = context.Resolve<IStateReader>();
                return new XdcStateSyncPivot(blockTree, syncConfig, stateReader, snapshotManager);
            });
        });

        SafeContext ctx = container.Resolve<SafeContext>();

        await ActivateAndWait(ctx);

        foreach (XdcBlockHeader gapBlock in gapBlocks)
        {
            snapshotManager.Received(1).StoreSnapshot(gapBlock);
        }

        IStateReader stateReader = container.Resolve<IStateReader>();
        foreach (XdcBlockHeader gapBlock in gapBlocks)
        {
            Assert.That(stateReader.HasStateForBlock(gapBlock), Is.True, $"gap block {gapBlock.Number} state must be synced");
        }
        Assert.That(stateReader.HasStateForBlock(xdcFinalPivot), Is.True, "final pivot state must be synced");
    }
}
