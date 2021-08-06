//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test
{
    public class NodeHealthServiceTests
    {
        [Test]
        public void CheckHealth_returns_expected_results([ValueSource(nameof(CheckHealthTestCases))] CheckHealthTest test)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ISyncServer syncServer = Substitute.For<ISyncServer>();
            IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
            IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
            IHealthHintService healthHintService = Substitute.For<IHealthHintService>();
            blockchainProcessor.IsProcessingBlocks(Arg.Any<ulong?>()).Returns(test.IsProcessingBlocks);
            blockProducer.IsProducingBlocks(Arg.Any<ulong?>()).Returns(test.IsProducingBlocks);
            syncServer.GetPeerCount().Returns(test.PeerCount);
            
            BlockHeaderBuilder GetBlockHeader(int blockNumber) => Build.A.BlockHeader.WithNumber(blockNumber);
            blockFinder.Head.Returns(new Block(GetBlockHeader(4).TestObject));
            if (test.IsSyncing)
            {
                blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(15).TestObject);
            }
            else
            {
                blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(2).TestObject);
            }
            

            NodeHealthService nodeHealthService =
                new(syncServer, blockFinder, blockchainProcessor, blockProducer, new HealthChecksConfig(),  healthHintService, test.IsMining);
            CheckHealthResult result = nodeHealthService.CheckHealth();
            Assert.AreEqual(test.ExpectedHealthy, result.Healthy);
            Assert.AreEqual(test.ExpectedMessage, FormatMessages(result.Messages.Select(x => x.Message)));
            Assert.AreEqual(test.ExpectedLongMessage, FormatMessages(result.Messages.Select(x => x.LongMessage)));
        }

        public class CheckHealthTest
        {
            public int Lp { get; set; }
            public int PeerCount { get; set; }

            public bool IsSyncing { get; set; }

            public bool IsMining { get; set; }

            public bool IsProducingBlocks { get; set; }

            public bool IsProcessingBlocks { get; set; }

            public bool ExpectedHealthy { get; set; }

            public string ExpectedMessage { get; set; }
            
            public string ExpectedLongMessage { get; set; }

            public override string ToString() =>
                $"Lp: {Lp} ExpectedHealthy: {ExpectedHealthy}, ExpectedDescription: {ExpectedMessage}, ExpectedLongDescription: {ExpectedLongMessage}";
        }
        
        public static IEnumerable<CheckHealthTest> CheckHealthTestCases
        {
            get
            {
                yield return new CheckHealthTest()
                {
                    Lp = 1,
                    IsSyncing = false,
                    IsProcessingBlocks = true,
                    PeerCount = 10,
                    ExpectedHealthy = true,
                    ExpectedMessage = "Fully synced. Peers: 10.",
                    ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 10."
                };
                yield return new CheckHealthTest()
                {
                    Lp = 2,
                    IsSyncing = false,
                    IsProcessingBlocks = true,
                    PeerCount = 0,
                    ExpectedHealthy = false,
                    ExpectedMessage = "Fully synced. Node is not connected to any peers.",
                    ExpectedLongMessage = "The node is now fully synced with a network. Node is not connected to any peers."
                };
                yield return new CheckHealthTest()
                {
                    Lp = 3,
                    IsSyncing = true,
                    PeerCount = 7,
                    ExpectedHealthy = false,
                    ExpectedMessage = "Still syncing. Peers: 7.",
                    ExpectedLongMessage = $"The node is still syncing, CurrentBlock: 4, HighestBlock: 15. The status will change to healthy once synced. Peers: 7."
                };
                yield return new CheckHealthTest()
                {
                    Lp = 4,
                    IsSyncing = false,
                    IsProcessingBlocks = false,
                    PeerCount = 7,
                    ExpectedHealthy = false,
                    ExpectedMessage = "Fully synced. Peers: 7. Stopped processing blocks.",
                    ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 7. The node stopped processing blocks."
                };
                yield return new CheckHealthTest()
                {
                    Lp = 5,
                    IsSyncing = true,
                    IsMining = true,
                    IsProducingBlocks = false,
                    IsProcessingBlocks = false,
                    PeerCount = 4,
                    ExpectedHealthy = true,
                    ExpectedMessage = "Still syncing. Peers: 4.",
                    ExpectedLongMessage = $"The node is still syncing, CurrentBlock: 4, HighestBlock: 15. The status will change to healthy once synced. Peers: 4."
                };
                yield return new CheckHealthTest()
                {
                    Lp = 6,
                    IsSyncing = true,
                    IsMining = true,
                    IsProducingBlocks = false,
                    IsProcessingBlocks = false,
                    PeerCount = 0,
                    ExpectedHealthy = false,
                    ExpectedMessage = "Still syncing. Node is not connected to any peers.",
                    ExpectedLongMessage = "The node is still syncing, CurrentBlock: 4, HighestBlock: 15. The status will change to healthy once synced. Node is not connected to any peers."
                };
                yield return new CheckHealthTest()
                {
                    Lp = 7,
                    IsSyncing = false,
                    IsMining = true,
                    IsProducingBlocks = false,
                    IsProcessingBlocks = false,
                    PeerCount = 1,
                    ExpectedHealthy = false,
                    ExpectedMessage = "Fully synced. Peers: 1. Stopped processing blocks. Stopped producing blocks.",
                    ExpectedLongMessage = "The node is now fully synced with a network. Peers: 1. The node stopped processing blocks. The node stopped producing blocks."
                };
                yield return new CheckHealthTest()
                {
                    Lp = 8,
                    IsSyncing = false,
                    IsMining = true,
                    IsProducingBlocks = true,
                    IsProcessingBlocks = true,
                    PeerCount = 1,
                    ExpectedHealthy = true,
                    ExpectedMessage = "Fully synced. Peers: 1.",
                    ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 1."
                };
            }
        }
        
        private static string FormatMessages(IEnumerable<string> messages)
        {
            if (messages.Any(x => !string.IsNullOrWhiteSpace(x)))
            {
                var joined = string.Join(". ", messages.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(joined))
                {
                    return joined + ".";
                }
            }
            
            return string.Empty;
        }
    }
}
