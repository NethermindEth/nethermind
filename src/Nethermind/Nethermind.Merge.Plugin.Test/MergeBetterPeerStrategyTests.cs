//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class MergeBetterPeerStrategyTests
{
    private const long TTD = 5;
    IPoSSwitcher _poSSwitcher;

    [SetUp]
    public void SetUp()
    {
        _poSSwitcher= Substitute.For<IPoSSwitcher>();
        _poSSwitcher.TerminalTotalDifficulty.Returns((UInt256)TTD);
    }   
    
    [TestCase(7,2,6,4, -1)]
    [TestCase(7,4,6,4, 0)]
    [TestCase(6,4,7,2, 1)]
    [TestCase(3,4,6,2, -1)]
    [TestCase(3,2,3,4, 0)]
    [TestCase(6,2,3,4, 1)]
    public void Compare_with_header_and_peer_return_expected_results(long totalDifficulty, long number, long peerTotalDifficulty, long peerNumber, int expectedResult)
    {
        TotalDifficultyBasedBetterPeerStrategy preMergeBetterPeerStrategy = new(null, LimboLogs.Instance);
        MergeBetterPeerStrategy betterPeerStrategy =
            new(preMergeBetterPeerStrategy, null, _poSSwitcher, LimboLogs.Instance);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);
        BlockHeader header = Build.A.BlockHeader.WithTotalDifficulty(totalDifficulty).WithNumber(number).TestObject;
        Assert.AreEqual(expectedResult, betterPeerStrategy.Compare(header, syncPeer));
    }
    
    [TestCase(7,2,6,4, -1)]
    [TestCase(7,4,6,4, 0)]
    [TestCase(6,4,7,2, 1)]
    [TestCase(3,4,6,2, -1)]
    [TestCase(3,2,3,4, 0)]
    [TestCase(6,2,3,4, 1)]
    public void Compare_with_value_and_peer_return_expected_results(long totalDifficulty, long number, long peerTotalDifficulty, long peerNumber, int expectedResult)
    {
        TotalDifficultyBasedBetterPeerStrategy preMergeBetterPeerStrategy = new(null, LimboLogs.Instance);
        MergeBetterPeerStrategy betterPeerStrategy =
            new(preMergeBetterPeerStrategy, null, _poSSwitcher, LimboLogs.Instance);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);
        Assert.AreEqual(expectedResult, betterPeerStrategy.Compare(((UInt256)totalDifficulty, number), syncPeer));
    }
    
    [TestCase(7,2,6,4, -1)]
    [TestCase(7,4,6,4, 0)]
    [TestCase(6,4,7,2, 1)]
    [TestCase(3,4,6,2, -1)]
    [TestCase(3,2,3,4, 0)]
    [TestCase(6,2,3,4, 1)]
    public void Compare_with_values_return_expected_results(long totalDifficulty, long number, long peerTotalDifficulty, long peerNumber, int expectedResult)
    {
        TotalDifficultyBasedBetterPeerStrategy preMergeBetterPeerStrategy = new(null, LimboLogs.Instance);
        MergeBetterPeerStrategy betterPeerStrategy =
            new(preMergeBetterPeerStrategy, null, _poSSwitcher, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.Compare(((UInt256)totalDifficulty, number), 
            ((UInt256)peerTotalDifficulty, peerNumber)));
    }

    [TestCase(6,4,7,2, false)]
    [TestCase(6,2,7,2, false)]
    [TestCase(7,2,7,4, true)]
    [TestCase(3,4,5,2, true)]
    [TestCase(3,2,3,4, false)]
    [TestCase(4,2,3,4, false)]
    public void IsBetterThanLocalChain_return_expected_results(long chainDifficulty, long bestFullBlock, long peerTotalDifficulty, long peerNumber, bool expectedResult)
    {
        ISyncProgressResolver resolver = Substitute.For<ISyncProgressResolver>();
        resolver.ChainDifficulty.Returns((UInt256)chainDifficulty);
        resolver.FindBestFullBlock().Returns(bestFullBlock);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);
        TotalDifficultyBasedBetterPeerStrategy preMergeBetterPeerStrategy = new(resolver, LimboLogs.Instance);
        MergeBetterPeerStrategy betterPeerStrategy =
            new(preMergeBetterPeerStrategy, resolver, _poSSwitcher, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.IsBetterThanLocalChain(((UInt256)peerTotalDifficulty, peerNumber)));
    }
    
    [TestCase(6,4,7,2, false)]
    [TestCase(6,2,7,2, false)]
    [TestCase(7,2,7,4, true)]
    [TestCase(3,4,5,2, true)]
    [TestCase(3,2,3,4, true)]
    [TestCase(4,2,3,4, false)]
    [TestCase(3,4,3,2, false)]
    public void IsDesiredPeer_return_expected_results(long chainDifficulty, long bestHeader, long peerTotalDifficulty, long peerNumber, bool expectedResult)
    {
        ISyncProgressResolver resolver = Substitute.For<ISyncProgressResolver>();
        resolver.ChainDifficulty.Returns((UInt256)chainDifficulty);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)peerTotalDifficulty);
        syncPeer.HeadNumber.Returns(peerNumber);
        TotalDifficultyBasedBetterPeerStrategy preMergeBetterPeerStrategy = new(resolver, LimboLogs.Instance);
        MergeBetterPeerStrategy betterPeerStrategy =
            new(preMergeBetterPeerStrategy, resolver, _poSSwitcher, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.IsDesiredPeer(((UInt256)peerTotalDifficulty, peerNumber),bestHeader));
    }

    [TestCase(null,true)]
    [TestCase(4,true)]
    [TestCase(5,false)]
    [TestCase(6,false)]
    public void IsLowerThanTerminalTotalDifficulty_return_expected_results(long totalDifficulty, bool expectedResult)
    {
        MergeBetterPeerStrategy betterPeerStrategy = new(null, null, _poSSwitcher, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.IsLowerThanTerminalTotalDifficulty((UInt256)totalDifficulty));
    }
    
}
