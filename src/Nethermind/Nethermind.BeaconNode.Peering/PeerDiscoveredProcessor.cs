// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
