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
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class SyncingSubscription : Subscription
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private bool IsSyncing { get; set; }
        private long BestSuggestedNumber { get; set; }
        
        public SyncingSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, IBlockTree? blockTree, ILogManager? logManager) 
            : base(jsonRpcDuplexClient)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            IsSyncing = CheckSyncing();
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id}: Syncing status on start is {IsSyncing}");
            
            _blockTree.NewBestSuggestedBlock += OnConditionsChange;
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will track NewBestSuggestedBlocks");
            
            _blockTree.NewHeadBlock += OnConditionsChange;
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will track NewHeadBlocks");
        }

        private bool CheckSyncing()
        {
            BestSuggestedNumber = _blockTree.FindBestSuggestedHeader().Number;
            return BestSuggestedNumber > _blockTree.Head.Number + 8;
        }

        private void OnConditionsChange(object? sender, BlockEventArgs e)
        {
            Task.Run(() =>
            {
                bool isSyncing = CheckSyncing();

                if (isSyncing == IsSyncing)
                {
                    if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} didn't changed syncing status: {IsSyncing}");
                    return;
                }

                if (_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} changed syncing status from {IsSyncing} to {isSyncing}");

                IsSyncing = isSyncing;
                JsonRpcResult result;

                if (isSyncing == false)
                {
                    result = CreateSubscriptionMessage(isSyncing);
                }
                else
                {
                    result = CreateSubscriptionMessage(new SyncingResult
                    {
                        IsSyncing = isSyncing,
                        CurrentBlock = _blockTree.Head.Number,
                        HighestBlock = BestSuggestedNumber,
                        StartingBlock = 0L
                    });
                }

                
                JsonRpcDuplexClient.SendJsonRpcResult(result);
                _logger.Trace($"Syncing subscription {Id} printed SyncingResult object.");
            }).ContinueWith(
                t =>
                    t.Exception?.Handle(ex =>
                    {
                        if (_logger.IsDebug) _logger.Debug($"Syncing subscription {Id}: Failed Task.Run.");
                        return true;
                    })
                , TaskContinuationOptions.OnlyOnFaulted
            );
        }
        
        public override SubscriptionType Type => SubscriptionType.Syncing;
        public override void Dispose()
        {
            _blockTree.NewBestSuggestedBlock -= OnConditionsChange;
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will no longer track NewBestSuggestedBlocks");

            _blockTree.NewHeadBlock -= OnConditionsChange;
            if(_logger.IsTrace) _logger.Trace($"Syncing subscription {Id} will no longer track NewHeadBlocks");
        }
    }
}
