// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TaikoEthSyncingInfoTests
{
    [TestCaseSource(nameof(GetFullInfoCases))]
    public SyncingResult GetFullInfo_ReturnsExpected(ulong? suggested, ulong? beacon, ulong? head, SyncMode innerMode)
    {
        IEthSyncingInfo inner = Substitute.For<IEthSyncingInfo>();
        inner.SyncMode.Returns(innerMode);

        return new TaikoEthSyncingInfo(BlockTreeWith(suggested, beacon, head), inner).GetFullInfo();
    }

    [Test]
    public void UpdateAndGetSyncTime_TracksTaikoIsSyncing_NotInner()
    {
        // Regression: inner.UpdateAndGetSyncTime() keys off the inner's beacon-unaware
        // IsSyncing(), which is `false` during the very plateau this decorator exists
        // to fix. The stopwatch must run on the decorator's corrected IsSyncing().
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestSuggestedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(1000UL).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(500UL).TestObject).TestObject);

        IEthSyncingInfo inner = Substitute.For<IEthSyncingInfo>();
        TaikoEthSyncingInfo info = new(blockTree, inner);

        Assert.That(info.UpdateAndGetSyncTime(), Is.EqualTo(TimeSpan.Zero), "first call: starts the stopwatch");
        Thread.Sleep(10);
        Assert.That(info.UpdateAndGetSyncTime(), Is.GreaterThan(TimeSpan.Zero), "subsequent call while syncing: elapsed");

        // Head catches the beacon pivot — decorator now reports not-syncing, stopwatch stops.
        blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(1000UL).TestObject).TestObject);
        Assert.That(info.UpdateAndGetSyncTime(), Is.EqualTo(TimeSpan.Zero), "after catch-up: stops");
        inner.DidNotReceive().UpdateAndGetSyncTime();
    }

    [Test]
    public void SyncMode_DelegatesToInner()
    {
        IEthSyncingInfo inner = Substitute.For<IEthSyncingInfo>();
        inner.SyncMode.Returns(SyncMode.WaitingForBlock);

        Assert.That(new TaikoEthSyncingInfo(Substitute.For<IBlockTree>(), inner).SyncMode, Is.EqualTo(SyncMode.WaitingForBlock));
    }

    private static IBlockTree BlockTreeWith(ulong? suggested, ulong? beacon, ulong? head)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBestSuggestedHeader().Returns(suggested is ulong s ? Build.A.BlockHeader.WithNumber(s).TestObject : null);
        blockTree.BestSuggestedBeaconHeader.Returns(beacon is ulong b ? Build.A.BlockHeader.WithNumber(b).TestObject : null);
        blockTree.Head.Returns(head is ulong h ? Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(h).TestObject).TestObject : null);
        return blockTree;
    }

    private static IEnumerable<TestCaseData> GetFullInfoCases()
    {
        // Regression: BeaconSync inserts headers via BestSuggestedBeaconHeader only, so
        // FindBestSuggestedHeader can lag (or be null) even after Head reaches the pivot.
        // Before the fix, isSyncing stuck at true once Head caught up.
        yield return new TestCaseData((ulong?)null, (ulong?)1000UL, (ulong?)1000UL, SyncMode.None)
            .Returns(SyncingResult.NotSyncing)
            .SetName("BeaconPivot_HeadCaughtUp_NotSyncing");

        yield return new TestCaseData((ulong?)null, (ulong?)1000UL, (ulong?)500UL, SyncMode.FastSync)
            .Returns(new SyncingResult { IsSyncing = true, CurrentBlock = 500UL, HighestBlock = 1000UL, SyncMode = SyncMode.FastSync })
            .SetName("BeaconPivot_HeadBehind_Syncing");

        yield return new TestCaseData((ulong?)null, (ulong?)null, (ulong?)null, SyncMode.None)
            .Returns(new SyncingResult { IsSyncing = true, CurrentBlock = 0UL, HighestBlock = 0UL, SyncMode = SyncMode.None })
            .SetName("Genesis_Syncing");

        // Both pointers populated — decorator must take max(suggested, beacon).
        yield return new TestCaseData((ulong?)2000UL, (ulong?)1000UL, (ulong?)500UL, SyncMode.None)
            .Returns(new SyncingResult { IsSyncing = true, CurrentBlock = 500UL, HighestBlock = 2000UL, SyncMode = SyncMode.None })
            .SetName("TakesMaxOfBothPointers");
    }
}
