// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.ServiceStopper;
using Nethermind.Network.P2P;

namespace Nethermind.Network
{
    public interface IPeerManager : IStoppableService
    {
        void Start();
        IReadOnlyCollection<Peer> ActivePeers { get; }
        IReadOnlyCollection<Peer> ConnectedPeers { get; }
        int MaxActivePeers { get; }
        int ActivePeersCount { get; }
        int ConnectedPeersCount { get; }

        /// <summary>
        /// Notifies the peer manager that a session completed the P2P Hello exchange and passed protocol
        /// validation, letting it enforce the active-peer capacity policy.
        /// </summary>
        /// <remarks>
        /// Sessions are admitted optimistically at the RLPx handshake (up to the hard-limit margin) so that
        /// the P2P protocol can initialize; this is the point where the peer manager decides whether the
        /// session fits within <see cref="MaxActivePeers"/> or is disconnected with a proper
        /// <c>TooManyPeers</c> message.
        /// </remarks>
        /// <param name="session">The session whose P2P protocol has been initialized and validated.</param>
        void OnP2PProtocolInitialized(ISession session);
    }
}
