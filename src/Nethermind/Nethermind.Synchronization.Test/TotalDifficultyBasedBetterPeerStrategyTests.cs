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
        TotalDifficultyBasedBetterPeerStrategy betterPeerStrategy = new(null, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.Compare(header, syncPeer));
    }
    
    [TestCase(9, -1)]
    [TestCase(10, 0)]
    [TestCase(11, 1)]
    public void Compare_with_value_and_peer_return_expected_results(long td, int expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)10);
        syncPeer.HeadNumber.Returns(10);
        TotalDifficultyBasedBetterPeerStrategy betterPeerStrategy = new(null, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.Compare(((UInt256)td, 10), syncPeer));
    }
    
    [TestCase(9, -1)]
    [TestCase(10, 0)]
    [TestCase(11, 1)]
    public void Compare_with_values_return_expected_results(long td, int expectedResult)
    {
        TotalDifficultyBasedBetterPeerStrategy betterPeerStrategy = new(null, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.Compare(((UInt256)td, 10), ((UInt256)10, 10)));
    }
    
    [TestCase(9, false)]
    [TestCase(10, false)]
    [TestCase(11, true)]
    public void IsBetterThanLocalChain_return_expected_results(long td, bool expectedResult)
    {
        ISyncProgressResolver resolver = Substitute.For<ISyncProgressResolver>();
        resolver.ChainDifficulty.Returns((UInt256)10);
        TotalDifficultyBasedBetterPeerStrategy betterPeerStrategy = new(resolver, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.IsBetterThanLocalChain(((UInt256)td, 10)));
    }
    
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
        TotalDifficultyBasedBetterPeerStrategy betterPeerStrategy = new(resolver, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.IsDesiredPeer(((UInt256)peerTotalDifficulty, peerNumber),bestHeader));
    }

    
    [Test]
    public void IsLowerThanTerminalTotalDifficulty_return_expected_results()
    {
        TotalDifficultyBasedBetterPeerStrategy betterPeerStrategy = new(null, LimboLogs.Instance);
        Assert.AreEqual(true, betterPeerStrategy.IsLowerThanTerminalTotalDifficulty((UInt256)10));
    }

}
