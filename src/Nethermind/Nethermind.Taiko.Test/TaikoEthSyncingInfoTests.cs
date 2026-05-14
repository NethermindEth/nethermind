// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
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
    public void GetFullInfo_ReturnsExpected(long? suggested, long? beacon, long? head, SyncMode innerMode, SyncingResult expected)
    {
        IEthSyncingInfo inner = Substitute.For<IEthSyncingInfo>();
        inner.SyncMode.Returns(innerMode);

        SyncingResult result = new TaikoEthSyncingInfo(BlockTreeWith(suggested, beacon, head), inner).GetFullInfo();

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void UpdateAndGetSyncTime_DelegatesToInner()
    {
        IEthSyncingInfo inner = Substitute.For<IEthSyncingInfo>();
        inner.UpdateAndGetSyncTime().Returns(TimeSpan.FromSeconds(42));

        TaikoEthSyncingInfo info = new(Substitute.For<IBlockTree>(), inner);

        Assert.That(info.UpdateAndGetSyncTime(), Is.EqualTo(TimeSpan.FromSeconds(42)));
        inner.Received(1).UpdateAndGetSyncTime();
    }

    [Test]
    public void SyncMode_DelegatesToInner()
    {
        IEthSyncingInfo inner = Substitute.For<IEthSyncingInfo>();
        inner.SyncMode.Returns(SyncMode.WaitingForBlock);

        Assert.That(new TaikoEthSyncingInfo(Substitute.For<IBlockTree>(), inner).SyncMode, Is.EqualTo(SyncMode.WaitingForBlock));
    }

    private static IBlockTree BlockTreeWith(long? suggested, long? beacon, long? head)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBestSuggestedHeader().Returns(suggested is long s ? Build.A.BlockHeader.WithNumber(s).TestObject : null);
        blockTree.BestSuggestedBeaconHeader.Returns(beacon is long b ? Build.A.BlockHeader.WithNumber(b).TestObject : null);
        blockTree.Head.Returns(head is long h ? Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(h).TestObject).TestObject : null);
        return blockTree;
    }

    private static IEnumerable<TestCaseData> GetFullInfoCases()
    {
        // Regression: BeaconSync inserts headers via BestSuggestedBeaconHeader only, so
        // FindBestSuggestedHeader can lag (or be null) even after Head reaches the pivot.
        // Before the fix, isSyncing stuck at true once Head caught up.
        yield return new TestCaseData(
            (long?)null, (long?)1000L, (long?)1000L, SyncMode.None,
            SyncingResult.NotSyncing
        ).SetName("BeaconPivot_HeadCaughtUp_NotSyncing");

        yield return new TestCaseData(
            (long?)null, (long?)1000L, (long?)500L, SyncMode.FastSync,
            new SyncingResult { IsSyncing = true, CurrentBlock = 500L, HighestBlock = 1000L, SyncMode = SyncMode.FastSync }
        ).SetName("BeaconPivot_HeadBehind_Syncing");

        yield return new TestCaseData(
            (long?)null, (long?)null, (long?)null, SyncMode.None,
            new SyncingResult { IsSyncing = true, CurrentBlock = 0L, HighestBlock = 0L, SyncMode = SyncMode.None }
        ).SetName("Genesis_Syncing");

        // Both pointers populated — decorator must take max(suggested, beacon).
        yield return new TestCaseData(
            (long?)2000L, (long?)1000L, (long?)500L, SyncMode.None,
            new SyncingResult { IsSyncing = true, CurrentBlock = 500L, HighestBlock = 2000L, SyncMode = SyncMode.None }
        ).SetName("TakesMaxOfBothPointers");
    }
}
