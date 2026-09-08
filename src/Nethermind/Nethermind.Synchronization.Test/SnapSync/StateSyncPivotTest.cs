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
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);

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

    [Test]
    public void Will_resolve_a_new_pivot_when_the_current_one_is_reorged_out()
    {
        BlockHeader orphaned = Build.A.BlockHeader.WithNumber(100).WithStateRoot(TestItem.KeccakA).TestObject;
        BlockHeader canonical = Build.A.BlockHeader.WithNumber(100).WithStateRoot(TestItem.KeccakB).TestObject;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
        blockTree.FindHeader(Arg.Any<ulong>()).Returns(orphaned, canonical);
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(ci => ReferenceEquals(ci.ArgAt<BlockHeader>(0), canonical));

        Synchronization.FastSync.StateSyncPivot stateSyncPivot = new(blockTree,
            new TestSyncConfig()
            {
                FastSync = true,
                StateMinDistanceFromHead = 32,
                StateMaxDistanceFromHead = 128,
            }, LimboLogs.Instance);
        blockTree.SyncPivot = (0UL, Keccak.Zero);

        Assert.That(stateSyncPivot.GetPivotHeader(), Is.SameAs(orphaned));

        // Head has not moved far enough to trigger the distance-based refresh, so without the canonicity
        // check the reorged-out header would be handed out forever.
        Assert.That(stateSyncPivot.GetPivotHeader(), Is.SameAs(canonical));
    }
}
