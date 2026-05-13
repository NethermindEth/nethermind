// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    /// <summary>
    /// Regression: on Taiko, BeaconSync inserts headers without bumping BestSuggestedHeader.
    /// Once Head catches the beacon pivot, isSyncing must return false even though
    /// FindBestSuggestedHeader() lags or returns null.
    /// </summary>
    [Test]
    public void GetFullInfo_BeaconPivot_HeadCaughtUp_NotSyncing()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBestSuggestedHeader().Returns((BlockHeader?)null);
        blockTree.BestSuggestedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(1000L).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(1000L).TestObject).TestObject);

        TaikoEthSyncingInfo info = new(blockTree, Substitute.For<IEthSyncingInfo>());
        SyncingResult result = info.GetFullInfo();

        Assert.That(result.IsSyncing, Is.False);
        Assert.That(result, Is.EqualTo(SyncingResult.NotSyncing));
    }

    /// <summary>Mid bulk-sync: beacon header ahead of Head → isSyncing=true with HighestBlock from beacon.</summary>
    [Test]
    public void GetFullInfo_BeaconPivot_HeadBehind_Syncing()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBestSuggestedHeader().Returns((BlockHeader?)null);
        blockTree.BestSuggestedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(1000L).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(500L).TestObject).TestObject);

        IEthSyncingInfo inner = Substitute.For<IEthSyncingInfo>();
        inner.SyncMode.Returns(SyncMode.FastSync);

        SyncingResult result = new TaikoEthSyncingInfo(blockTree, inner).GetFullInfo();

        Assert.That(result.IsSyncing, Is.True);
        Assert.That(result.CurrentBlock, Is.EqualTo(500L));
        Assert.That(result.HighestBlock, Is.EqualTo(1000L));
        Assert.That(result.SyncMode, Is.EqualTo(SyncMode.FastSync));
    }

    /// <summary>Genesis: nothing seen yet → isSyncing=true with zero pointers.</summary>
    [Test]
    public void GetFullInfo_Genesis_Syncing()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBestSuggestedHeader().Returns((BlockHeader?)null);
        blockTree.BestSuggestedBeaconHeader.Returns((BlockHeader?)null);
        blockTree.Head.Returns((Block?)null);

        SyncingResult result = new TaikoEthSyncingInfo(blockTree, Substitute.For<IEthSyncingInfo>()).GetFullInfo();

        Assert.That(result.IsSyncing, Is.True);
        Assert.That(result.CurrentBlock, Is.Zero);
        Assert.That(result.HighestBlock, Is.Zero);
    }

    /// <summary>Engine-API path advanced BestSuggestedHeader past beacon: max picks suggested.</summary>
    [Test]
    public void GetFullInfo_TakesMaxOfBothPointers()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(2000L).TestObject);
        blockTree.BestSuggestedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(1000L).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(500L).TestObject).TestObject);

        SyncingResult result = new TaikoEthSyncingInfo(blockTree, Substitute.For<IEthSyncingInfo>()).GetFullInfo();

        Assert.That(result.IsSyncing, Is.True);
        Assert.That(result.HighestBlock, Is.EqualTo(2000L));
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
}
