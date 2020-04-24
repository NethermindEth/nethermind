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
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public class PeerDiscoveredProcessor : QueueProcessorBase<string>
    {
        private const int MaximumQueue = 1024;
        private readonly ILogger _logger;
        private readonly PeerManager _peerManager;
        private readonly ISynchronizationManager _synchronizationManager;

        public PeerDiscoveredProcessor(ILogger<PeerDiscoveredProcessor> logger,
            ISynchronizationManager synchronizationManager,
            PeerManager peerManager)
            : base(logger, MaximumQueue)
        {
            _logger = logger;
            _synchronizationManager = synchronizationManager;
            _peerManager = peerManager;
        }

        public void Enqueue(string peerId)
        {
            EnqueueItem(peerId);
        }

        protected override async Task ProcessItemAsync(string rpcMessage)
        {
            try
            {
                if (_logger.IsDebug())
                    LogDebug.ProcessPeerDiscovered(_logger, rpcMessage, null);

                Session session = _peerManager.AddPeerSession(rpcMessage);

                if (session.Direction == ConnectionDirection.Out)
                {
                    await _synchronizationManager.OnPeerDialOutConnected(rpcMessage).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.HandlePeerDiscoveredError(_logger, rpcMessage, ex.Message, ex);
            }
        }
    }
}