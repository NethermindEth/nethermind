// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class SyncingSubscription : Subscription
    {
        private readonly IBlockTree _blockTree;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private bool _lastIsSyncing;

        public SyncingSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IBlockTree? blockTree,
            IEthSyncingInfo ethSyncingInfo,
            ILogManager? logManager)
            : base(jsonRpcDuplexClient)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _lastIsSyncing = _ethSyncingInfo.IsSyncing();
            if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id}: Syncing status on start is {_lastIsSyncing}");

            _blockTree.NewBestSuggestedBlock += OnConditionsChange;
            if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will track NewBestSuggestedBlocks");

            _blockTree.NewHeadBlock += OnConditionsChange;
            if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will track NewHeadBlocks");
        }

        private class SubscriptionSyncingResult
        {
            public bool? IsSyncing { get; set; }
            public long? StartingBlock { get; set; }
            public long? CurrentBlock { get; set; }
            public long? HighestBlock { get; set; }
        }

        private void OnConditionsChange(object? sender, BlockEventArgs e)
        {
            ScheduleAction(() =>
            {
                SyncingResult syncingResult = _ethSyncingInfo.GetFullInfo();
                bool isSyncing = syncingResult.IsSyncing;

                if (isSyncing == _lastIsSyncing)
                {
                    if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} didn't changed syncing status: {_lastIsSyncing}");
                    return;
                }

                if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} changed syncing status from {_lastIsSyncing} to {isSyncing}");

                _lastIsSyncing = isSyncing;
                JsonRpcResult result;

                if (isSyncing == false)
                {
                    result = CreateSubscriptionMessage(isSyncing);
                }
                else
                {
                    result = CreateSubscriptionMessage(new SubscriptionSyncingResult()
                    {
                        IsSyncing = syncingResult.IsSyncing,
                        StartingBlock = syncingResult.StartingBlock,
                        CurrentBlock = syncingResult.CurrentBlock,
                        HighestBlock = syncingResult.HighestBlock
                    });
                }


                JsonRpcDuplexClient.SendJsonRpcResult(result);
                _logger.Trace($"Syncing subscription {Id} printed SyncingResult object.");
            });
        }

        public override string Type => SubscriptionType.Syncing;
        public override void Dispose()
        {
            _blockTree.NewBestSuggestedBlock -= OnConditionsChange;
            _blockTree.NewHeadBlock -= OnConditionsChange;
            base.Dispose();

            if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will no longer track NewBestSuggestedBlocks");
            if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will no longer track NewHeadBlocks");
        }
    }
}
