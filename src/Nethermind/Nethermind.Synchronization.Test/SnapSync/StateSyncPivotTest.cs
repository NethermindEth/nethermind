// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class StateSyncPivotTest
{
    [Test]
    public void Does_not_finalize_a_pivot_replaced_by_a_same_height_reorg()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader originalHeader = Build.A.BlockHeader.WithNumber(100).WithHash(Keccak.Compute("original pivot")).TestObject;
        BlockHeader reorgedHeader = Build.A.BlockHeader.WithNumber(100).WithHash(Keccak.Compute("reorged pivot")).TestObject;
        BlockHeader canonicalHeader = originalHeader;

        blockTree.BestSuggestedHeader.Returns(originalHeader);
        blockTree.FindHeader(Arg.Any<ulong>()).Returns(_ => canonicalHeader);
        blockTree.SyncPivot.Returns((0UL, Keccak.Zero));

        Synchronization.FastSync.StateSyncPivot stateSyncPivot = new(blockTree, new TestSyncConfig
        {
            StateMinDistanceFromHead = 0,
            StateMaxDistanceFromHead = 100,
        }, LimboLogs.Instance);

        Assert.That(stateSyncPivot.GetPivotHeader(), Is.EqualTo(originalHeader));

        canonicalHeader = reorgedHeader;
        stateSyncPivot.UpdateHeaderForcefully();

        Assert.Multiple(() =>
        {
            Assert.That(stateSyncPivot.GetPivotHeader(), Is.EqualTo(reorgedHeader));
            Assert.That(stateSyncPivot.CanFinalize(originalHeader), Is.False);
            Assert.That(stateSyncPivot.CanFinalize(reorgedHeader), Is.True);
        });
    }

    [TestCase(1000UL, 1000UL, 10UL, 100UL, 1000UL, 0UL)]
    [TestCase(900UL, 1000UL, 10UL, 50UL, 1000UL, 0UL)]
    [TestCase(900UL, 1000UL, 10UL, 100UL, 1000UL, 0UL)]
    [TestCase(900UL, 900UL, 32UL, 100UL, 900UL, 0UL)]
    [TestCase(0UL, 300UL, 32UL, 100UL, 301UL, 300UL)]
    public void Will_set_new_best_header_some_distance_from_best_suggested(
        ulong originalBestSuggested,
        ulong newBestSuggested,
        ulong minDistance,
        ulong maxDistance,
        ulong newPivotHeader,
        ulong syncPivot
    )
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<ulong>())
            .Returns(static (ci) => Build.A.BlockHeader.WithNumber(ci.ArgAt<ulong>(0)).TestObject);

        Synchronization.FastSync.StateSyncPivot stateSyncPivot = new(blockTree,
            new TestSyncConfig()
            {
                PivotNumber = syncPivot,
                FastSync = true,
                StateMinDistanceFromHead = minDistance,
                StateMaxDistanceFromHead = maxDistance,
            }, LimboLogs.Instance);
        blockTree.SyncPivot = (syncPivot, Keccak.Zero);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(originalBestSuggested).TestObject);
        Assert.That(stateSyncPivot.GetPivotHeader(), Is.Not.Null);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(newBestSuggested).TestObject);
        Assert.That(stateSyncPivot.GetPivotHeader()?.Number, Is.EqualTo(newPivotHeader));
    }
}
