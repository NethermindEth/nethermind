// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
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
            EnqueueItem(rpcMessage);
        }

        protected override async Task ProcessItemAsync(RpcMessage<BeaconBlocksByRange> rpcMessage)
        {
            try
            {
                if (rpcMessage.Direction == RpcDirection.Request)
                {
                    if (_logger.IsDebug())
                        LogDebug.ProcessBeaconBlocksByRange(_logger, rpcMessage.Content, null);

                    // TODO: Add some sanity checks on request to prevent DoS
                    // TODO: Maybe add limit on number of blocks (as allowed by spec)

                    Slot slot = new Slot(rpcMessage.Content.StartSlot +
                                         rpcMessage.Content.Step * (rpcMessage.Content.Count - 1));
                    Stack<(Root root, SignedBeaconBlock signedBlock)> signedBlocks =
                        new Stack<(Root, SignedBeaconBlock)>();
                    // Search backwards from head for the requested slots
                    Root root = rpcMessage.Content.HeadBlockRoot;
                    while (slot >= rpcMessage.Content.StartSlot)
                    {
                        root = await _forkChoice.GetAncestorAsync(_store, root, slot);
                        SignedBeaconBlock signedBlock = await _store.GetSignedBlockAsync(root);
                        if (signedBlock.Message.Slot == slot)
                        {
                            signedBlocks.Push((root, signedBlock));
                        }
                        else
                        {
                            // block is skipped
                            if (_logger.IsWarn())
                                Log.RequestedBlockSkippedSlot(_logger, slot, rpcMessage.Content.HeadBlockRoot, null);
                        }

                        // If they requested for slot 0, then include it (anchor block is usually null), but don't underflow
                        if (slot == Slot.Zero)
                        {
                            break;
                        }
                        slot = slot - Slot.One;
                    }

                    // Send each block in a response chunk, in slot order
                    foreach (var data in signedBlocks)
                    {
                        if (_logger.IsDebug())
                            LogDebug.SendingRequestBlocksByRangeResponse(_logger, data.signedBlock.Message, data.root,
                                null);

                        await _networkPeering.SendBlockAsync(rpcMessage.PeerId, data.signedBlock);
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
