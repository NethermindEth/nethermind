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

using System;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;

namespace Nethermind.JsonRpc.Services
{
    public class CheckHealthResult
    {
        public bool Healthy { get; set; }

        public string Description { get; set; }

        public string LongDescription { get; set; }
    }

    public class HealthService : IHealthService
    {
        private readonly IEthModule _ethModule;
        private readonly INetModule _netModule;
        private readonly ISyncConfig _syncConfig;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IBlockProducer _blockProducer;

        public HealthService(IEthModule ethModule, INetModule netModule, ISyncConfig syncConfig, IBlockchainProcessor blockchainProcessor, IBlockProducer blockProducer)
        {
            _ethModule = ethModule;
            _netModule = netModule;
            _syncConfig = syncConfig;
            _blockchainProcessor = blockchainProcessor;
            _blockProducer = blockProducer;
        }

        public CheckHealthResult CheckHealth()
        {
            string description = string.Empty;
            string longDescription = string.Empty;
            bool healthy = true;
            long netPeerCount = (long)_netModule.net_peerCount().GetData();
            SyncingResult ethSyncing = (SyncingResult)_ethModule.eth_syncing().GetData();

            // if (IsValidator())
            // {
            //     if (ethSyncing.IsSyncing == false && IsProducingBlocks())
            // }

            if (ethSyncing.IsSyncing == false && netPeerCount > 0)
            {
                healthy = true;
                longDescription = $"The node is now fully synced with a network, number of peers: {netPeerCount}";
            }
            else if (ethSyncing.IsSyncing == false && netPeerCount == 0)
            {
                healthy = false;
                longDescription = $"The node has 0 peers connected";
            }

            return new CheckHealthResult()
            {
                Healthy = healthy, Description = description, LongDescription = longDescription
            };
        }
    }
}
