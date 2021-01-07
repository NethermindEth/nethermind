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
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Services;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Services
{
    public class HealthServiceTests
    {
        [Test]
        public void CheckHealth_returns_expectedresults([ValueSource(nameof(CheckHealthTestCases))] CheckHealthTest test)
        {
            IEthModule ethModule = Substitute.For<IEthModule>();
            INetModule netModule = Substitute.For<INetModule>();
            ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
            IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
            IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
            netModule.net_peerCount().GetData().Returns(test.PeerCount);
            ethModule.eth_syncing().GetData().Returns(new SyncingResult() {IsSyncing = test.IsSyncing});
            HealthService healthService =
                new HealthService(ethModule, netModule, syncConfig, blockchainProcessor, blockProducer);
            CheckHealthResult result = healthService.CheckHealth();
            Assert.AreEqual(test.ExpectedHealthy, result.Healthy);
            Assert.AreEqual(test.ExpectedDescription, result.Description);
            Assert.AreEqual(test.ExpectedLongDescription, result.LongDescription);
        }

        public class CheckHealthTest
        {
            public int PeerCount { get; set; }

            public bool IsSyncing { get; set; }

            public bool IsMining { get; set; }

            public bool IsProducingBlocks { get; set; }

            public bool IsProcessingBlocks { get; set; }

            public bool ExpectedHealthy { get; set; }

            public string ExpectedDescription { get; set; }
            
            public string ExpectedLongDescription { get; set; }

            public override string ToString() =>
                $"ExpectedHealthy: {ExpectedHealthy}, ExpectedDescription: {ExpectedDescription}, ExpectedLongDescription: {ExpectedLongDescription}";
        }
        
        public static IEnumerable<CheckHealthTest> CheckHealthTestCases
        {
            get
            {
                yield return new CheckHealthTest()
                {
                    IsSyncing = false,
                    IsProcessingBlocks = true,
                    PeerCount = 10,
                    ExpectedHealthy = true,
                    ExpectedDescription = null,
                    ExpectedLongDescription = $"The node is now fully synced with a network, number of peers: 10"
                };
                yield return new CheckHealthTest()
                {
                    IsSyncing = false,
                    IsProcessingBlocks = true,
                    PeerCount = 0,
                    ExpectedHealthy = false,
                    ExpectedDescription = "Node is not connected to any peers.",
                    ExpectedLongDescription = "Node is not connected to any peers."
                };
                yield return new CheckHealthTest()
                {
                    IsSyncing = true,
                    PeerCount = 7,
                    ExpectedHealthy = false,
                    ExpectedDescription = "Still syncing.",
                    ExpectedLongDescription = $"The node is still syncing, CurrentBlock: 0, HighestBlock: 0, Peers: 7"
                };
                yield return new CheckHealthTest()
                {
                    IsSyncing = false,
                    IsProcessingBlocks = false,
                    PeerCount = 7,
                    ExpectedHealthy = false,
                    ExpectedDescription = "Stopped handling blocks.",
                    ExpectedLongDescription = $"The node stopped processing blocks."
                };
                yield return new CheckHealthTest()
                {
                    IsSyncing = true,
                    IsMining = true,
                    IsProducingBlocks = false,
                    IsProcessingBlocks = false,
                    PeerCount = 4,
                    ExpectedHealthy = true,
                    ExpectedDescription = "Still syncing.",
                    ExpectedLongDescription = $"The node is still syncing, CurrentBlock: 0, HighestBlock: 0, Peers: 4"
                };
                yield return new CheckHealthTest()
                {
                    IsSyncing = true,
                    IsMining = true,
                    IsProducingBlocks = false,
                    IsProcessingBlocks = false,
                    PeerCount = 0,
                    ExpectedHealthy = false,
                    ExpectedDescription = "Node is not connected to any peers.",
                    ExpectedLongDescription = "Node is not connected to any peers."
                };
                yield return new CheckHealthTest()
                {
                    IsSyncing = false,
                    IsMining = true,
                    IsProducingBlocks = false,
                    IsProcessingBlocks = false,
                    PeerCount = 1,
                    ExpectedHealthy = false,
                    ExpectedDescription = "Stopped producing blocks.",
                    ExpectedLongDescription = "The node stopped producing blocks."
                };
                yield return new CheckHealthTest()
                {
                    IsSyncing = false,
                    IsMining = true,
                    IsProducingBlocks = true,
                    IsProcessingBlocks = false,
                    PeerCount = 1,
                    ExpectedHealthy = true,
                    ExpectedDescription = null,
                    ExpectedLongDescription = $"The node is now fully synced with a network, number of peers: 1"
                };
            }
        }
    }
}
