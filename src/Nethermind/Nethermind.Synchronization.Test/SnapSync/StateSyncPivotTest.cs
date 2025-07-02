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
    [TestCase(1000, 1000, 10, 100, 1000, 0)]
    [TestCase(900, 1000, 10, 50, 1000, 0)]
    [TestCase(900, 1000, 10, 100, 1000, 0)]
    [TestCase(900, 900, 32, 100, 900, 0)]
    [TestCase(0, 300, 32, 100, 301, 300)]
    public void Will_set_new_best_header_some_distance_from_best_suggested(
        int originalBestSuggested,
        int newBestSuggested,
        int minDistance,
        int maxDistance,
        int newPivotHeader,
        int syncPivot
    )
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<long>())
            .Returns(static (ci) => Build.A.BlockHeader.WithNumber((long)ci[0]).TestObject);

        Synchronization.FastSync.StateSyncPivot stateSyncPivot = new Synchronization.FastSync.StateSyncPivot(blockTree,
            new TestSyncConfig()
            {
                PivotNumber = syncPivot.ToString(),
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
