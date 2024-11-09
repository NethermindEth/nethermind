// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class PivotTest
{
    [TestCase(1000, 1000, 10, 100, 990)]
    [TestCase(900, 1000, 10, 50, 990)]
    [TestCase(900, 1000, 10, 100, 990)]
    public void Will_set_new_best_header_some_distance_from_best_suggested(
        int originalBestSuggested,
        int newBestSuggested,
        int minDistance,
        int maxDistance,
        int newBestHeader
    )
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<long>())
            .Returns((ci) => Build.A.BlockHeader.WithNumber((long)ci[0]).TestObject);

        Nethermind.Synchronization.SnapSync.Pivot pivot = new Nethermind.Synchronization.SnapSync.Pivot(blockTree,
            new SyncConfig()
            {
                StateMinDistanceFromHead = minDistance,
                StateMaxDistanceFromHead = maxDistance,
            }, LimboLogs.Instance);

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(originalBestSuggested).TestObject);
        pivot.GetPivotHeader().Should().NotBeNull();

        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(newBestSuggested).TestObject);
        pivot.GetPivotHeader().Number.Should().Be(newBestHeader);
    }
}
