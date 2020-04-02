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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public class PeerDiscoveredProcessor : QueueProcessorBase<string>
    {
        private readonly PeerManager _peerManager;

        private readonly ISynchronizationManager _synchronizationManager;

        public PeerDiscoveredProcessor(ILogger<PeerDiscoveredProcessor> logger,
            ISynchronizationManager synchronizationManager,
            PeerManager peerManager)
            : base(logger)
        {
            _logger = logger;
            _synchronizationManager = synchronizationManager;
            _peerManager = peerManager;
        }

        protected override async Task ProcessItemAsync(string peerId)
        {
            try
            {
                if (_logger.IsDebug())
                    LogDebug.ProcessPeerDiscovered(_logger, peerId, null);

                Session session = _peerManager.AddPeerSession(peerId);

                if (session.Direction == ConnectionDirection.Out)
                {
                    await _synchronizationManager.OnPeerDialOutConnected(peerId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.HandlePeerDiscoveredError(_logger, peerId, ex.Message, ex);
            }
        }

        public void Enqueue(string peerId)
        {
            ChannelWriter.TryWrite(peerId);
        }
    }
}