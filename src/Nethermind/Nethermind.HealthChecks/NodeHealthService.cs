// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using Nethermind.Api;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.HealthChecks
{
    public class CheckHealthResult
    {
        public bool Healthy { get; set; }
        public ICollection<(string Message, string LongMessage)> Messages { get; set; }
        public bool IsSyncing { get; set; }
        public ICollection<string> Errors { get; set; }
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
        private readonly IRpcCapabilitiesProvider _rpcCapabilitiesProvider;
        private readonly IDriveInfo[] _drives;
        private readonly bool _isMining;

        public NodeHealthService(ISyncServer syncServer,
            IBlockchainProcessor blockchainProcessor,
            IBlockProducer blockProducer,
            IHealthChecksConfig healthChecksConfig,
            IHealthHintService healthHintService,
            IEthSyncingInfo ethSyncingInfo,
            IRpcCapabilitiesProvider rpcCapabilitiesProvider,
            INethermindApi api,
            IDriveInfo[] drives,
            bool isMining)
        {
            _syncServer = syncServer;
            _isMining = isMining;
            _healthChecksConfig = healthChecksConfig;
            _healthHintService = healthHintService;
            _blockchainProcessor = blockchainProcessor;
            _blockProducer = blockProducer;
            _ethSyncingInfo = ethSyncingInfo;
            _rpcCapabilitiesProvider = rpcCapabilitiesProvider;
            _api = api;
            _drives = drives;
        }

        public CheckHealthResult CheckHealth()
        {
            List<(string Message, string LongMessage)> messages = new();
            List<string> errors = new();
            bool healthy = false;
            long netPeerCount = _syncServer.GetPeerCount();
            SyncingResult syncingResult = _ethSyncingInfo.GetFullInfo();

            if (_api.SpecProvider!.TerminalTotalDifficulty is not null)
            {
                bool syncHealthy = CheckSyncPostMerge(messages, errors, syncingResult);

                bool hasPeers = CheckPeers(messages, errors, netPeerCount);

                bool clAlive = CheckClAlive();

                if (!clAlive)
                {
                    AddClUnavailableMessage(messages, errors);
                }

                healthy = syncHealthy & clAlive & hasPeers;
            }
            else
            {
                if (!_isMining && syncingResult.IsSyncing)
                {
                    AddStillSyncingMessage(messages, syncingResult);
                    CheckPeers(messages, errors, netPeerCount);
                }
                else if (!_isMining && !syncingResult.IsSyncing)
                {
                    AddFullySyncMessage(messages);
                    bool peers = CheckPeers(messages, errors, netPeerCount);
                    bool processing = IsProcessingBlocks(messages, errors);
                    healthy = peers && processing;
                }
                else if (_isMining && syncingResult.IsSyncing)
                {
                    AddStillSyncingMessage(messages, syncingResult);
                    healthy = CheckPeers(messages, errors, netPeerCount);
                }
                else if (_isMining && !syncingResult.IsSyncing)
                {
                    AddFullySyncMessage(messages);
                    bool peers = CheckPeers(messages, errors, netPeerCount);
                    bool processing = IsProcessingBlocks(messages, errors);
                    bool producing = IsProducingBlocks(messages, errors);
                    healthy = peers && processing && producing;
                }
            }

            bool isLowDiskSpaceErrorAdded = false;
            for (int index = 0; index < _drives.Length; index++)
            {
                IDriveInfo drive = _drives[index];
                double freeSpacePercentage = drive.GetFreeSpacePercentage();
                if (freeSpacePercentage < _healthChecksConfig.LowStorageSpaceWarningThreshold)
                {
                    AddLowDiskSpaceMessage(messages, drive, freeSpacePercentage);
                    if (!isLowDiskSpaceErrorAdded)
                    {
                        errors.Add(ErrorStrings.LowDiskSpace);
                        isLowDiskSpaceErrorAdded = true;
                    }
                    healthy = false;
                }
            }

            return new CheckHealthResult() { Healthy = healthy, Errors = errors, Messages = messages, IsSyncing = syncingResult.IsSyncing };
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

        private bool CheckSyncPostMerge(ICollection<(string Description, string LongDescription)> messages,
            ICollection<string> errors, SyncingResult syncingResult)
        {
            if (syncingResult.IsSyncing)
            {
                if (syncingResult.SyncMode == SyncMode.Disconnected)
                {
                    messages.Add(("Sync degraded",
                        $"Sync degraded(no useful peers), CurrentBlock: {syncingResult.CurrentBlock}, HighestBlock: {syncingResult.HighestBlock}"));
                    errors.Add(ErrorStrings.SyncDegraded);
                    return false;
                }
                messages.Add(("Still syncing",
                    $"The node is still syncing, CurrentBlock: {syncingResult.CurrentBlock}, HighestBlock: {syncingResult.HighestBlock}"));
            }
            else
            {
                AddFullySyncMessage(messages);
            }

            return true;
        }

        public bool CheckClAlive()
        {
            var now = _api.Timestamper.UtcNow;
            var capabilities = _rpcCapabilitiesProvider.GetEngineCapabilities();
            bool result = false;
            foreach (var capability in capabilities)
            {
                if (capability.Value)
                {
                    result |= UpdateStatsAndCheckInvoked(capability.Key, now);
                }
            }
            return result;
        }

        private readonly ConcurrentDictionary<string, DateTime> _previousSuccessfulCheckTime = new();
        private readonly ConcurrentDictionary<string, int> _previousMethodCallSuccesses = new();

        private bool UpdateStatsAndCheckInvoked(string methodName, DateTime now)
        {
            var methodCallSuccesses = _api.JsonRpcLocalStats!.GetMethodStats(methodName).Successes;
            var previousSuccesses = _previousMethodCallSuccesses.GetOrAdd(methodName, 0);
            var lastSuccessfulCheckTime = _previousSuccessfulCheckTime.GetOrAdd(methodName, now);

            if (methodCallSuccesses == previousSuccesses)
            {
                int diff = (int)(Math.Floor((now - lastSuccessfulCheckTime).TotalSeconds));
                return diff <= _healthChecksConfig.MaxIntervalClRequestTime;
            }

            _previousSuccessfulCheckTime[methodName] = now;
            _previousMethodCallSuccesses[methodName] = methodCallSuccesses;
            return true;
        }

        private static class ErrorStrings
        {
            public const string NoPeers = nameof(NoPeers);
            public const string NotProducingBlocks = nameof(NotProducingBlocks);
            public const string NotProcessingBlocks = nameof(NotProcessingBlocks);
            public const string ClUnavailable = nameof(ClUnavailable);
            public const string LowDiskSpace = nameof(LowDiskSpace);
            public const string SyncDegraded = nameof(SyncDegraded);
        }

        private static bool CheckPeers(ICollection<(string Description, string LongDescription)> messages,
            ICollection<string> errors, long netPeerCount)
        {
            bool hasPeers = netPeerCount > 0;
            if (hasPeers == false)
            {
                errors.Add(ErrorStrings.NoPeers);
                messages.Add(("Node is not connected to any peers", "Node is not connected to any peers"));
            }
            else
            {
                messages.Add(($"Peers: {netPeerCount}", $"Peers: {netPeerCount}"));
            }

            return hasPeers;
        }

        private bool IsProducingBlocks(ICollection<(string Description, string LongDescription)> messages, ICollection<string> errors)
        {
            ulong? maxIntervalHint = GetBlockProducerIntervalHint();
            bool producingBlocks = _blockProducer.IsProducingBlocks(maxIntervalHint);
            if (producingBlocks == false)
            {
                errors.Add(ErrorStrings.NotProducingBlocks);
                messages.Add(("Stopped producing blocks", "The node stopped producing blocks"));
            }

            return producingBlocks;
        }

        private bool IsProcessingBlocks(ICollection<(string Description, string LongDescription)> messages, ICollection<string> errors)
        {
            ulong? maxIntervalHint = GetBlockProcessorIntervalHint();
            bool processingBlocks = _blockchainProcessor.IsProcessingBlocks(maxIntervalHint);
            if (processingBlocks == false)
            {
                errors.Add(ErrorStrings.NotProcessingBlocks);
                messages.Add(("Stopped processing blocks", "The node stopped processing blocks"));
            }

            return processingBlocks;
        }

        private static void AddClUnavailableMessage(ICollection<(string Description, string LongDescription)> messages, ICollection<string> errors)
        {
            errors.Add(ErrorStrings.ClUnavailable);
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

        private static void AddLowDiskSpaceMessage(ICollection<(string Description, string LongDescription)> messages, IDriveInfo drive, double freeSpacePercent)
        {
            messages.Add(("Low free disk space", $"The node is running out of free disk space in '{drive.RootDirectory.FullName}' - only {drive.GetFreeSpaceInGiB():F2} GB ({freeSpacePercent:F2}%) left"));
        }
    }
}
