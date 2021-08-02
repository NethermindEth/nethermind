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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.Synchronization;

namespace Nethermind.HealthChecks
{
    public class CheckHealthResult
    {
        public bool Healthy { get; set; }

        public ICollection<(string Message, string LongMessage)> Messages { get; set; }
    }

    public class NodeHealthService : INodeHealthService
    {
        private readonly ISyncServer _syncServer;
        private readonly IBlockFinder _blockFinder;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IBlockProducer _blockProducer;
        private readonly IHealthChecksConfig _healthChecksConfig;
        private readonly IHealthHintService _healthHintService;
        private readonly bool _isMining;

        public NodeHealthService(
            ISyncServer syncServer,
            IBlockFinder blockFinder,
            IBlockchainProcessor blockchainProcessor,
            IBlockProducer blockProducer,
            IHealthChecksConfig healthChecksConfig,
            IHealthHintService healthHintService,
            bool isMining)
        {
            _syncServer = syncServer;
            _blockFinder = blockFinder;
            _isMining = isMining;
            _healthChecksConfig = healthChecksConfig;
            _healthHintService = healthHintService;
            _blockchainProcessor = blockchainProcessor;
            _blockProducer = blockProducer;
        }

        public CheckHealthResult CheckHealth()
        {
            List<(string Message, string LongMessage)> messages = new List<(string Message, string LongMessage)>();
            bool healthy = false;
            long netPeerCount = _syncServer.GetPeerCount();
            SyncingResult syncingResult = IsSyncing();

            if (_isMining == false && syncingResult.IsSyncing)
            {
                healthy = false;
                AddStillSyncingMessage(messages, syncingResult);
                CheckPeers(messages, netPeerCount);
            }
            else if (_isMining == false && syncingResult.IsSyncing == false)
            {
                AddFullySyncMessage(messages);
                bool peers = CheckPeers(messages, netPeerCount);
                bool processing = IsProcessingBlocks(messages);
                healthy = peers && processing;
            }
            else if (_isMining && syncingResult.IsSyncing)
            {
                AddStillSyncingMessage(messages, syncingResult);
                healthy = CheckPeers(messages, netPeerCount);
            }
            else if (_isMining && syncingResult.IsSyncing == false)
            {
                AddFullySyncMessage(messages);
                bool peers = CheckPeers(messages, netPeerCount);
                bool processing = IsProcessingBlocks(messages);
                bool producing = IsProducingBlocks(messages);
                healthy = peers && processing && producing;
            }

            return new CheckHealthResult() {Healthy = healthy, Messages = messages};
        }

        private ulong? GetBlockProcessorIntervalHint()
        {
            return _healthChecksConfig.MaxIntervalWithoutProcessedBlock ??
                   _healthHintService.MaxSecondsIntervalForProcessingBlocksHint();
        }

        private ulong? GetBlockProducerIntervalHint()
        {
            return _healthChecksConfig.MaxIntervalWithoutProducedBlock ??
                   _healthHintService.MaxSecondsIntervalForProducingBlocksHint();
        }

        private SyncingResult IsSyncing()
        {
            SyncingResult result;
            long bestSuggestedNumber = _blockFinder.FindBestSuggestedHeader().Number;
            bool isSyncing = bestSuggestedNumber > _blockFinder.Head.Number + 8;

            if (isSyncing)
            {
                result = new SyncingResult
                {
                    CurrentBlock = _blockFinder.Head.Number,
                    HighestBlock = bestSuggestedNumber,
                    StartingBlock = 0L,
                    IsSyncing = true
                };
            }
            else
            {
                result = SyncingResult.NotSyncing;
            }

            return result;
        }

        private static bool CheckPeers(ICollection<(string Description, string LongDescription)> messages,
            long netPeerCount)
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

        private bool IsProducingBlocks(ICollection<(string Description, string LongDescription)> messages)
        {
            ulong? maxIntervalHint = GetBlockProducerIntervalHint();
            bool producingBlocks = _blockProducer.IsProducingBlocks(maxIntervalHint);
            if (producingBlocks == false)
            {
                messages.Add(("Stopped producing blocks", "The node stopped producing blocks"));
            }

            return producingBlocks;
        }

        private bool IsProcessingBlocks(ICollection<(string Description, string LongDescription)> messages)
        {
            ulong? maxIntervalHint = GetBlockProcessorIntervalHint();
            bool processingBlocks = _blockchainProcessor.IsProcessingBlocks(maxIntervalHint);
            if (processingBlocks == false)
            {
                messages.Add(("Stopped processing blocks", "The node stopped processing blocks"));
            }

            return processingBlocks;
        }

        private static void AddStillSyncingMessage(ICollection<(string Description, string LongDescription)> messages,
            SyncingResult ethSyncing)
        {
            messages.Add(("Still syncing",
                $"The node is still syncing, CurrentBlock: {ethSyncing.CurrentBlock}, HighestBlock: {ethSyncing.HighestBlock}. The status will change to healthy once synced"));
        }

        private static void AddFullySyncMessage(ICollection<(string Description, string LongDescription)> messages)
        {
            messages.Add(("Fully synced", $"The node is now fully synced with a network"));
        }
    }
}
