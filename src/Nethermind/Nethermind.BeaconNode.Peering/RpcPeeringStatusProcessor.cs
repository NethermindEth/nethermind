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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.P2p;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public class RpcPeeringStatusProcessor : QueueProcessorBase<RpcMessage<PeeringStatus>>
    {
        private readonly ILogger _logger;
        private readonly PeerManager _peerManager;
        private readonly ISynchronizationManager _synchronizationManager;
        private const int MaximumQueue = 1024;

        public RpcPeeringStatusProcessor(ILogger<RpcPeeringStatusProcessor> logger,
            ISynchronizationManager synchronizationManager,
            PeerManager peerManager)
            : base(logger, MaximumQueue)
        {
            _logger = logger;
            _synchronizationManager = synchronizationManager;
            _peerManager = peerManager;
        }

        public void Enqueue(RpcMessage<PeeringStatus> statusRpcMessage)
        {
            EnqueueItem(statusRpcMessage);
        }

        protected override async Task ProcessItemAsync(RpcMessage<PeeringStatus> rpcMessage)
        {
            try
            {
                PeerInfo peerInfo = _peerManager.UpdatePeerStatus(rpcMessage.PeerId, rpcMessage.Content);
                Session session = _peerManager.OpenSession(peerInfo);

                // Mothra seems to be raising all incoming RPC (sent as request and as response)
                // with requestResponseFlag 0, so check here if already have the status so we don't go into infinite loop
                // => So use session details instead of the status message
                //if (statusRpcMessage.Direction == RpcDirection.Request)
                if (session.Direction == ConnectionDirection.Out)
                {
                    // If it is a dial out, we must have already sent the status request and this is the response
                    await _synchronizationManager.OnStatusResponseReceived(rpcMessage.PeerId,
                        rpcMessage.Content);
                }
                else
                {
                    await _synchronizationManager.OnStatusRequestReceived(rpcMessage.PeerId,
                        rpcMessage.Content);
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