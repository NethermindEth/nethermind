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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public class RpcBeaconBlocksByRangeProcessor : QueueProcessorBase<RpcMessage<BeaconBlocksByRange>>
    {
        private readonly IForkChoice _forkChoice;
        private readonly ILogger _logger;
        private readonly INetworkPeering _networkPeering;
        private readonly IStore _store;
        private const int MaximumQueue = 1024;

        public RpcBeaconBlocksByRangeProcessor(ILogger<RpcBeaconBlocksByRangeProcessor> logger,
            INetworkPeering networkPeering,
            IForkChoice forkChoice,
            IStore store)
            : base(logger, MaximumQueue)
        {
            _logger = logger;
            _networkPeering = networkPeering;
            _forkChoice = forkChoice;
            _store = store;
        }

        public void Enqueue(RpcMessage<BeaconBlocksByRange> rpcMessage)
        {
            ChannelWriter.TryWrite(rpcMessage);
        }

        protected override async Task ProcessItemAsync(RpcMessage<BeaconBlocksByRange> rpcMessage)
        {
            try
            {
                if (rpcMessage.Direction == RpcDirection.Request)
                {
                    Slot slot = new Slot(rpcMessage.Content.StartSlot +
                                         rpcMessage.Content.Step * (rpcMessage.Content.Count - 1));
                    Stack<BeaconBlock> blocks = new Stack<BeaconBlock>();
                    // Search backwards from head for the requested slots
                    Root root = rpcMessage.Content.HeadBlockRoot;
                    while (slot >= rpcMessage.Content.StartSlot)
                    {
                        root = await _forkChoice.GetAncestorAsync(_store, root, slot);
                        BeaconBlock block = await _store.GetBlockAsync(root);
                        if (block.Slot == slot)
                        {
                            blocks.Push(block);
                        }
                        else
                        {
                            // block is skipped
                        }
                    }

                    // Send each block in a response chunk, in slot order
                    foreach (BeaconBlock block in blocks)
                    {
                        await _networkPeering.SendBlockAsync(rpcMessage.PeerId, block);
                    }
                }
                else
                {
                    throw new Exception($"Unexpected direction {rpcMessage.Direction}");
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.HandleRpcStatusError(_logger, rpcMessage.PeerId, ex.Message, ex);
            }
        }
    }
}