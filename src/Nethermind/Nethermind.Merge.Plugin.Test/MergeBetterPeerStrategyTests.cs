// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class MergeBetterPeerStrategyTests
{
    [TestCase(7, 2ul, 6, 4ul, -1)]
    [TestCase(7, 4ul, 6, 4ul, 0)]
    [TestCase(6, 4ul, 7, 2ul, 1)]
    [TestCase(3, 4ul, 6, 2ul, -1)]
    [TestCase(3, 2ul, 3, 4ul, 0)]
    [TestCase(6, 2ul, 3, 4ul, 1)]
    public void Compare_with_header_and_peer_return_expected_results(long totalDifficulty, ulong number, long peerTotalDifficulty, ulong peerNumber, int expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);
        BlockHeader header = Build.A.BlockHeader.WithTotalDifficulty(totalDifficulty).WithNumber(number).TestObject;

        MergeBetterPeerStrategy betterPeerStrategy = CreateStrategy();

        Assert.That(betterPeerStrategy.Compare(header, syncPeer), Is.EqualTo(expectedResult));
    }

    [TestCase(7, 2ul, 6, 4ul, -1)]
    [TestCase(7, 4ul, 6, 4ul, 0)]
    [TestCase(6, 4ul, 7, 2ul, 1)]
    [TestCase(3, 4ul, 6, 2ul, -1)]
    [TestCase(3, 2ul, 3, 4ul, 0)]
    [TestCase(6, 2ul, 3, 4ul, 1)]
    public void Compare_with_value_and_peer_return_expected_results(long totalDifficulty, ulong number, long peerTotalDifficulty, ulong peerNumber, int expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);

        MergeBetterPeerStrategy betterPeerStrategy = CreateStrategy();
        Assert.That(betterPeerStrategy.Compare(((UInt256)totalDifficulty, number), syncPeer), Is.EqualTo(expectedResult));
    }

    [TestCase(7, 2ul, 6, 4ul, -1)]
    [TestCase(7, 4ul, 6, 4ul, 0)]
    [TestCase(6, 4ul, 7, 2ul, 1)]
    [TestCase(3, 4ul, 6, 2ul, -1)]
    [TestCase(3, 2ul, 3, 4ul, 0)]
    [TestCase(6, 2ul, 3, 4ul, 1)]
    public void Compare_with_values_return_expected_results(long totalDifficulty, ulong number, long peerTotalDifficulty, ulong peerNumber, int expectedResult)
    {
        MergeBetterPeerStrategy betterPeerStrategy = CreateStrategy();
        Assert.That(betterPeerStrategy.Compare(((UInt256)totalDifficulty, number), ((UInt256)peerTotalDifficulty, peerNumber)), Is.EqualTo(expectedResult));
    }

    [TestCase(6, 4ul, 7, 2ul, false)]
    [TestCase(6, 2ul, 7, 2ul, false)]
    [TestCase(7, 2ul, 7, 4ul, true)]
    [TestCase(3, 4ul, 5, 2ul, true)]
    [TestCase(3, 2ul, 3, 4ul, false)]
    [TestCase(4, 2ul, 3, 4ul, false)]
    public void IsBetterThanLocalChain_return_expected_results(long chainDifficulty, ulong bestFullBlock, long peerTotalDifficulty, ulong peerNumber, bool expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);

        MergeBetterPeerStrategy betterPeerStrategy = CreateStrategy();
        Assert.That(betterPeerStrategy.IsBetterThanLocalChain(((UInt256)peerTotalDifficulty, peerNumber), ((UInt256)chainDifficulty, bestFullBlock)), Is.EqualTo(expectedResult));
    }

    [TestCase(6, 4ul, 7, 2ul, false)]
    [TestCase(6, 2ul, 7, 2ul, false)]
    [TestCase(3, 4ul, 5, 2ul, true)]
    [TestCase(3, 2ul, 3, 4ul, true)]
    [TestCase(4, 2ul, 3, 4ul, false)]
    [TestCase(3, 4ul, 3, 2ul, false)]
    public void IsDesiredPeer_return_expected_results_pre_ttd(long chainDifficulty, ulong bestHeader, long peerTotalDifficulty, ulong peerNumber, bool expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);

        MergeBetterPeerStrategy betterPeerStrategy = CreateStrategy();
        Assert.That(betterPeerStrategy.IsDesiredPeer(((UInt256)peerTotalDifficulty, peerNumber), ((UInt256)chainDifficulty, bestHeader)), Is.EqualTo(expectedResult));
    }

    [TestCase(9ul, 7ul, 4ul, 7, 10ul, true)]
    [TestCase(9ul, 8ul, 2ul, 7, 7ul, false)]
    [TestCase(null, 9ul, 4ul, 5, 99ul, false)]
    [TestCase(3ul, 5ul, 1ul, 3, 4ul, true)]
    public void IsDesiredPeer_return_expected_results_post_ttd(ulong? pivotNumber, ulong chainDifficulty, ulong bestHeader, long peerTotalDifficulty, ulong peerNumber, bool expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);

        MergeBetterPeerStrategy betterPeerStrategy = CreateStrategy(pivotNumber);
        Assert.That(betterPeerStrategy.IsDesiredPeer(((UInt256)peerTotalDifficulty, peerNumber), ((UInt256)chainDifficulty, bestHeader)), Is.EqualTo(expectedResult));
    }

    [TestCase(0, true)]
    [TestCase(4, true)]
    [TestCase(5, false)]
    [TestCase(6, false)]
    public void IsLowerThanTerminalTotalDifficulty_return_expected_results(long totalDifficulty, bool expectedResult)
    {
        MergeBetterPeerStrategy betterPeerStrategy = CreateStrategy();
        Assert.That(betterPeerStrategy.IsLowerThanTerminalTotalDifficulty((UInt256)totalDifficulty), Is.EqualTo(expectedResult));
    }

    private MergeBetterPeerStrategy CreateStrategy(ulong? beaconPivotNum = null)
    {
        const long ttd = 5;
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TerminalTotalDifficulty.Returns((UInt256)ttd);

        IBeaconPivot beaconPivot = Substitute.For<IBeaconPivot>();
        if (beaconPivotNum is not null)
        {
            beaconPivot.BeaconPivotExists().Returns(true);
            beaconPivot.PivotNumber.Returns(beaconPivotNum.Value);
        }

        TotalDifficultyBetterPeerStrategy preMergeBetterPeerStrategy = new(LimboLogs.Instance);
        MergeBetterPeerStrategy betterPeerStrategy = new(preMergeBetterPeerStrategy, poSSwitcher, beaconPivot, LimboLogs.Instance);
        return betterPeerStrategy;
    }
}
