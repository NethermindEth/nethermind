// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class TotalDifficultyBasedBetterPeerStrategyTests
{

    [TestCase(9, -1)]
    [TestCase(10, 0)]
    [TestCase(11, 1)]
    public void Compare_with_header_and_peer_return_expected_results(long td, int expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)10);
        syncPeer.HeadNumber.Returns(10);
        BlockHeader header = Build.A.BlockHeader.WithTotalDifficulty(td).TestObject;

        TotalDifficultyBetterPeerStrategy betterPeerStrategy = new(LimboLogs.Instance);
        Assert.That(betterPeerStrategy.Compare(header, syncPeer), Is.EqualTo(expectedResult));
    }

    [TestCase(9, -1)]
    [TestCase(10, 0)]
    [TestCase(11, 1)]
    public void Compare_with_value_and_peer_return_expected_results(long td, int expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)10);
        syncPeer.HeadNumber.Returns(10);

        TotalDifficultyBetterPeerStrategy betterPeerStrategy = new(LimboLogs.Instance);
        Assert.That(betterPeerStrategy.Compare(((UInt256)td, 10), syncPeer), Is.EqualTo(expectedResult));
    }

    [TestCase(9, -1)]
    [TestCase(10, 0)]
    [TestCase(11, 1)]
    public void Compare_with_values_return_expected_results(long td, int expectedResult)
    {
        TotalDifficultyBetterPeerStrategy betterPeerStrategy = new(LimboLogs.Instance);
        Assert.That(betterPeerStrategy.Compare(((UInt256)td, 10), ((UInt256)10, 10)), Is.EqualTo(expectedResult));
    }

    [TestCase(9, false)]
    [TestCase(10, false)]
    [TestCase(11, true)]
    public void IsBetterThanLocalChain_return_expected_results(long td, bool expectedResult)
    {
        TotalDifficultyBetterPeerStrategy betterPeerStrategy = new(LimboLogs.Instance);
        Assert.That(betterPeerStrategy.IsBetterThanLocalChain(((UInt256)td, 10), ((UInt256)10, 10)), Is.EqualTo(expectedResult));
    }

    [TestCase(3, 4, 5, 2, true)]
    [TestCase(3, 2, 3, 4, true)]
    [TestCase(4, 2, 3, 4, false)]
    [TestCase(3, 4, 3, 2, false)]
    public void IsDesiredPeer_return_expected_results(long chainDifficulty, long bestHeader, long peerTotalDifficulty, long peerNumber, bool expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);

        TotalDifficultyBetterPeerStrategy betterPeerStrategy = new(LimboLogs.Instance);
        Assert.That(betterPeerStrategy.IsDesiredPeer(((UInt256)peerTotalDifficulty, peerNumber), ((UInt256)chainDifficulty, bestHeader)), Is.EqualTo(expectedResult));
    }


    [Test]
    public void IsLowerThanTerminalTotalDifficulty_return_expected_results()
    {
        IBetterPeerStrategy betterPeerStrategy = new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance);
        Assert.That(betterPeerStrategy.IsLowerThanTerminalTotalDifficulty((UInt256)10), Is.EqualTo(true));
    }

}
