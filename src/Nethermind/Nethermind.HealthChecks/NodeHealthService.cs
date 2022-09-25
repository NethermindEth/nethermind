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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Api;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Facade.Eth;
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
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IBlockProducer _blockProducer;
        private readonly IHealthChecksConfig _healthChecksConfig;
        private readonly IHealthHintService _healthHintService;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private readonly INethermindApi _api;
        private readonly bool _isMining;

        public NodeHealthService(
            ISyncServer syncServer,
            IBlockchainProcessor blockchainProcessor,
            IBlockProducer blockProducer,
            IHealthChecksConfig healthChecksConfig,
            IHealthHintService healthHintService,
            IEthSyncingInfo ethSyncingInfo,
            INethermindApi api,
            bool isMining)
        {
            _syncServer = syncServer;
            _isMining = isMining;
            _healthChecksConfig = healthChecksConfig;
            _healthHintService = healthHintService;
            _blockchainProcessor = blockchainProcessor;
            _blockProducer = blockProducer;
            _ethSyncingInfo = ethSyncingInfo;
            _api = api;
        }

        public CheckHealthResult CheckHealth()
        {
            List<(string Message, string LongMessage)> messages = new();
            bool healthy = false;
            long netPeerCount = _syncServer.GetPeerCount();
            SyncingResult syncingResult = _ethSyncingInfo.GetFullInfo();

            if (_api.SpecProvider!.TerminalTotalDifficulty != null)
            {
                if (syncingResult.IsSyncing)
                {
                    AddStillSyncingMessage(messages, syncingResult);
                }
                else
                {
                    AddFullySyncMessage(messages);
                }
                bool hasPeers = CheckPeers(messages, netPeerCount);

                bool clAlive = CheckClAlive();

                if (!clAlive)
                {
                    AddClUnavailableMessage(messages);
                }

                healthy = !syncingResult.IsSyncing & clAlive & hasPeers;
            }
            else
            {
                if (!_isMining && syncingResult.IsSyncing)
                {
                    AddStillSyncingMessage(messages, syncingResult);
                    CheckPeers(messages, netPeerCount);
                }
                else if (!_isMining && !syncingResult.IsSyncing)
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
                else if (_isMining && !syncingResult.IsSyncing)
                {
                    AddFullySyncMessage(messages);
                    bool peers = CheckPeers(messages, netPeerCount);
                    bool processing = IsProcessingBlocks(messages);
                    bool producing = IsProducingBlocks(messages);
                    healthy = peers && processing && producing;
                }
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

        public bool CheckClAlive()
        {
            var now = _api.Timestamper.UtcNow;
            bool forkchoice = CheckMethodInvoked("engine_forkchoiceUpdatedV1", now);
            bool newPayload = CheckMethodInvoked("engine_newPayloadV1", now);
            bool exchangeTransition = CheckMethodInvoked("engine_exchangeTransitionConfigurationV1", now);
            return forkchoice || newPayload || exchangeTransition;
        }

        private readonly ConcurrentDictionary<string, bool> _previousMethodCheckResult = new();
        private readonly ConcurrentDictionary<string, DateTime> _previousSuccessfulCheckTime = new();
        private readonly ConcurrentDictionary<string, int> _previousMethodCallSuccesses = new();

        private bool CheckMethodInvoked(string methodName, DateTime now)
        {
            var methodCallSuccesses = _api.JsonRpcLocalStats!.GetMethodStats(methodName).Successes;
            var previousCheckResult = _previousMethodCheckResult.GetOrAdd(methodName, true);
            var previousSuccesses = _previousMethodCallSuccesses.GetOrAdd(methodName, 0);
            var lastSuccessfulCheckTime = _previousSuccessfulCheckTime.GetOrAdd(methodName, now);

            if (methodCallSuccesses == previousSuccesses)
            {
                int diff = (int)(Math.Floor((now - lastSuccessfulCheckTime).TotalSeconds));
                if (diff > _healthChecksConfig.MaxIntervalClRequestTime)
                {
                    _previousMethodCheckResult[methodName] = false;
                    return false;
                }

                return previousCheckResult;
            }

            _previousSuccessfulCheckTime[methodName] = now;
            _previousMethodCheckResult[methodName] = true;
            _previousMethodCallSuccesses[methodName] = methodCallSuccesses;
            return true;
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

        private static void AddClUnavailableMessage(ICollection<(string Description, string LongDescription)> messages)
        {
            messages.Add(("No messages from CL", "No new messages from CL after last check"));
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
