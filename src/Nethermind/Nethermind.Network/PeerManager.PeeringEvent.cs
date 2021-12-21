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

using System.Threading;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Stats;
// ReSharper disable InconsistentNaming

namespace Nethermind.Network;

public partial class PeerManager
{
    /// <summary>
    /// Peering event is something that needs to be handled in the peer manager main loop.
    /// Thing of it as an event flowing into the processing channel.
    /// </summary>
    private abstract class PeeringEvent
    {
        protected INetworkConfig _networkConfig => _peerManager._networkConfig;
        protected readonly PeerComparer _peerComparer = new();
        protected readonly PeerManager _peerManager;
        protected ILogger _logger => _peerManager._logger;
        protected INodeStatsManager _stats => _peerManager._stats;
        protected IPeerPool _peerPool => _peerManager._peerPool;
        protected int AvailableActivePeersCount => _peerManager.AvailableActivePeersCount;
        protected int MaxActivePeers => _peerManager.MaxActivePeers;
        protected CancellationTokenSource _cancellationTokenSource => _peerManager._cancellationTokenSource;
        protected void AddSession(ISession session, Peer peer) => _peerManager.AddSession(session, peer);

        protected void ToggleSessionEventListeners(ISession session, bool shouldListen)
            => _peerManager.ToggleSessionEventListeners(session, shouldListen);

        protected void DeactivatePeerIfDisconnected(Peer activePeer, string reason)
            => _peerManager.DeactivatePeerIfDisconnected(activePeer, reason);

        protected PeeringEvent(PeerManager peerManager)
        {
            _peerManager = peerManager;
        }

        public abstract void Execute();
    }
}
