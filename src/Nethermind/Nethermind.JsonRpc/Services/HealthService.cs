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
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;

namespace Nethermind.JsonRpc.Services
{
    public class CheckHealthResult
    {
        public bool Healthy { get; set; }

        public ICollection<(string Message, string LongMessage)> Messages { get; set; }
    }

    public class HealthService : IHealthService
    {
        private readonly IRpcModuleProvider _rpcModuleProvider;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IBlockProducer _blockProducer;
        private readonly bool _isMining;

        public HealthService(IRpcModuleProvider rpcModuleProvider, IBlockchainProcessor blockchainProcessor, IBlockProducer blockProducer, bool isMining)
        {
            _rpcModuleProvider = rpcModuleProvider;
            _isMining = isMining;
            _blockchainProcessor = blockchainProcessor;
            _blockProducer = blockProducer;
        }

        public async Task<CheckHealthResult> CheckHealth()
        {
            IEthModule ethModule = (IEthModule) await _rpcModuleProvider.Rent("eth_syncing", false);
            INetModule netModule = (INetModule) await _rpcModuleProvider.Rent("net_peerCount", false);
            List<(string Message, string LongMessage)> messages = new List<(string Message, string LongMessage)>();
            bool healthy = false;
            long netPeerCount = (long)netModule.net_peerCount().GetData();
            SyncingResult ethSyncing = (SyncingResult)ethModule.eth_syncing().GetData();
            
            if (_isMining == false && ethSyncing.IsSyncing)
            {
                healthy = false;
                AddStillSyncingMessage(messages, ethSyncing);
                CheckPeers(messages, netPeerCount);
            }
            else if (_isMining == false && ethSyncing.IsSyncing == false)
            {
                AddFullySyncMessage(messages);
                bool peers = CheckPeers(messages, netPeerCount);
                bool processing = IsProcessingBlocks(messages, _blockchainProcessor.IsProcessingBlocks);
                healthy = peers && processing;
            }
            else if (_isMining && ethSyncing.IsSyncing)
            {
                AddStillSyncingMessage(messages, ethSyncing);
                healthy = CheckPeers(messages, netPeerCount);
            }
            else if (_isMining && ethSyncing.IsSyncing == false)
            {
                AddFullySyncMessage(messages);
                bool peers = CheckPeers(messages, netPeerCount);
                bool processing = IsProcessingBlocks(messages, _blockchainProcessor.IsProcessingBlocks);
                bool producing = IsProducingBlocks(messages, _blockProducer.IsProducingBlocks);
                healthy = peers && processing && producing;
            }
            
            _rpcModuleProvider.Return("eth_syncing", ethModule);
            _rpcModuleProvider.Return("net_peerCount", netModule);
            
            return new CheckHealthResult()
            {
                Healthy = healthy, 
                Messages = messages
            };
        }

        private static bool CheckPeers(ICollection<(string Description, string LongDescription)> messages, long netPeerCount)
        {
            bool hasPeers = netPeerCount > 0;
            if (hasPeers == false)
            {
                messages.Add(("Node is not connected to any peers", "Node is not connected to any peers"));  
            }
            else
            {
                messages.Add(($"Peers: {netPeerCount}", $"Peers: {netPeerCount}"));
            }

            return hasPeers;
        }
        
        private static bool IsProducingBlocks(ICollection<(string Description, string LongDescription)> messages, bool producingBlocks)
        {
            if (producingBlocks == false)
            {
                messages.Add(("Stopped producing blocks", "The node stopped producing blocks"));  
            }

            return producingBlocks;
        }
        
        private static bool IsProcessingBlocks(ICollection<(string Description, string LongDescription)> messages, bool processingBlocks)
        {
            if (processingBlocks == false)
            {
                messages.Add(("Stopped processing blocks", "The node stopped processing blocks"));  
            }

            return processingBlocks;
        }
        
        private static void AddStillSyncingMessage(ICollection<(string Description, string LongDescription)> messages,  SyncingResult ethSyncing)
        {
            messages.Add(("Still syncing", $"The node is still syncing, CurrentBlock: {ethSyncing.CurrentBlock}, HighestBlock: {ethSyncing.HighestBlock}"));
        }
        
        private static void AddFullySyncMessage(ICollection<(string Description, string LongDescription)> messages)
        {
            messages.Add(("Fully synced", $"The node is now fully synced with a network"));
        }

        private static string FormatMessages(IEnumerable<string> messages)
        {
            return messages.Any() ? string.Join(". ", messages) + "." : string.Empty;
        }
    }
}
