// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class StateSyncPivotTest
{
    [TestCase(1000ul, 1000ul, 10, 100, 1000ul, 0ul)]
    [TestCase(900ul, 1000ul, 10, 50, 1000ul, 0ul)]
    [TestCase(900ul, 1000ul, 10, 100, 1000ul, 0ul)]
    [TestCase(900ul, 900ul, 32, 100, 900ul, 0ul)]
    [TestCase(0ul, 300ul, 32, 100, 301ul, 300ul)]
    public void Will_set_new_best_header_some_distance_from_best_suggested(
        ulong originalBestSuggested,
        ulong newBestSuggested,
        int minDistance,
        int maxDistance,
        ulong newPivotHeader,
        ulong syncPivot
    )
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>())
            .Returns(static ci => Build.A.BlockHeader.WithNumber(ci.ArgAt<ulong>(0)).TestObject);

        Synchronization.FastSync.StateSyncPivot stateSyncPivot = new Synchronization.FastSync.StateSyncPivot(blockTree,
            new TestSyncConfig()
            {
                PivotNumber = syncPivot,
                FastSync = true,
                StateMinDistanceFromHead = minDistance,
                StateMaxDistanceFromHead = maxDistance,
            }, LimboLogs.Instance);
        blockTree.SyncPivot = (syncPivot, Keccak.Zero);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(originalBestSuggested).TestObject);
        stateSyncPivot.GetPivotHeader().Should().NotBeNull();

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(newBestSuggested).TestObject);
        stateSyncPivot.GetPivotHeader()?.Number.Should().Be(newPivotHeader);
    }
}
