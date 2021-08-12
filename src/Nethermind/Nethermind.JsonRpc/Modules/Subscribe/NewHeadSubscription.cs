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
    public class NewHeadSubscription : Subscription
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public NewHeadSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, IBlockTree? blockTree, ILogManager? logManager) 
            : base(jsonRpcDuplexClient)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
            _blockTree.BlockAddedToMain += OnBlockAddedToMain;
            if(_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} will track BlockAddedToMain");
        }

        private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            Task.Run(() =>
            {
                JsonRpcResult result = CreateSubscriptionMessage(new BlockForRpc(e.Block, false));

                JsonRpcDuplexClient.SendJsonRpcResult(result);
                if(_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} printed new block");
            }).ContinueWith(
                t =>
                    t.Exception?.Handle(ex =>
                    {
                        if (_logger.IsDebug) _logger.Debug($"NewHeads subscription {Id}: Failed Task.Run after BlockAddedToMain event.");
                        return true;
                    })
                , TaskContinuationOptions.OnlyOnFaulted
            );
        }

        public override SubscriptionType Type => SubscriptionType.NewHeads;
        
        public override void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
            if(_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} will no longer track BlockAddedToMain");
        }
    }
}
