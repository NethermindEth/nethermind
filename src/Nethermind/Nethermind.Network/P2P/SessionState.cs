// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P
{
    public enum SessionState
    {
        /// <summary>
        /// Newly created session object
        /// </summary>
        New = 0,

        /// <summary>
        /// RLPx handshake complete
        /// </summary>
        HandshakeComplete = 1,

        /// <summary>
        /// P2P Initialized
        /// </summary>
        Initialized = 2,

        /// <summary>
        /// Disconnecting all subprotocols
        /// </summary>
        DisconnectingProtocols = 3,

        /// <summary>
        /// Disconnecting P2P protocols.
        /// </summary>
        Disconnecting = 4,

        /// <summary>
        /// Disconnected.
        /// </summary>
        Disconnected = 5
    }
}
