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

using Nethermind.Blockchain.Find;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Eth
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class EthSyncingInfoTests
    {
        [Test]
        public void GetFullInfo_WhenNotSyncing()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178001L).TestObject);
            blockFinder.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000L).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockFinder);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.AreEqual(false, syncingResult.IsSyncing);
            Assert.AreEqual(0, syncingResult.CurrentBlock);
            Assert.AreEqual(0, syncingResult.HighestBlock);
            Assert.AreEqual(0, syncingResult.StartingBlock);
        }
        
        [Test]
        public void GetFullInfo_WhenSyncing()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(6178010L).TestObject);
            blockFinder.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(6178000L).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockFinder);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.AreEqual(true, syncingResult.IsSyncing);
            Assert.AreEqual(6178000L, syncingResult.CurrentBlock);
            Assert.AreEqual(6178010, syncingResult.HighestBlock);
            Assert.AreEqual(0, syncingResult.StartingBlock);
        }
        
        [TestCase(6178001L, 6178000L, false)]
        [TestCase(6178010L, 6178000L, true)]
        public void IsSyncing_ReturnsExpectedResult(long bestHeader, long currentHead, bool expectedResult)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(bestHeader).TestObject);
            blockFinder.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(currentHead).TestObject).TestObject);
            EthSyncingInfo ethSyncingInfo = new(blockFinder);
            SyncingResult syncingResult = ethSyncingInfo.GetFullInfo();
            Assert.AreEqual(expectedResult, syncingResult.IsSyncing);
        }
    }
}
