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

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class SyncingSubscription : Subscription
    {
        private readonly IBlockTree _blockTree;
        private readonly IEthSyncingInfo _ethSyncingInfo;
        private bool _lastIsSyncing;
        
        public SyncingSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, IBlockTree? blockTree, IEthSyncingInfo ethSyncingInfo, ILogManager? logManager) 
            : base(jsonRpcDuplexClient)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            _lastIsSyncing = _ethSyncingInfo.IsSyncing();
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id}: Syncing status on start is {_lastIsSyncing}");
            
            _blockTree.NewBestSuggestedBlock += OnConditionsChange;
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will track NewBestSuggestedBlocks");
            
            _blockTree.NewHeadBlock += OnConditionsChange;
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will track NewHeadBlocks");
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
                    result = CreateSubscriptionMessage(syncingResult);
                }


                JsonRpcDuplexClient.SendJsonRpcResult(result);
                _logger.Trace($"Syncing subscription {Id} printed SyncingResult object.");
            });
        }

        protected override string GetErrorMsg()
        {
            return $"Syncing subscription {Id}: Failed Task.Run.";
        }
        
        public override string Type => SubscriptionType.Syncing;
        public override void Dispose()
        {
            _blockTree.NewBestSuggestedBlock -= OnConditionsChange;
            _blockTree.NewHeadBlock -= OnConditionsChange;
            base.Dispose();
            
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will no longer track NewBestSuggestedBlocks");
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will no longer track NewHeadBlocks");
        }
    }
}
