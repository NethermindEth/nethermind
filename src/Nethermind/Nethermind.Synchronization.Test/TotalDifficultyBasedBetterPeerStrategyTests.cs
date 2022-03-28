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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class TotalDifficultyBasedBetterPeerStrategyTests
{
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
    
    [TestCase(9, -1)]
    [TestCase(10, 0)]
    [TestCase(11, 1)]
    public void Compare_return_expected_results(long td, int expectedResult)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns((UInt256)10);
        syncPeer.HeadNumber.Returns(10);
        TotalDifficultyBasedBetterPeerStrategy betterPeerStrategy = new(null, LimboLogs.Instance);
        Assert.AreEqual(expectedResult, betterPeerStrategy.Compare(((UInt256)td, 10), syncPeer));
    }
}
