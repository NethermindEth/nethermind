// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconNode.Peering
{
    public enum SessionState
    {
        /// <summary>
        /// Newly created session object
        /// </summary>
        New = 0,

        /// <summary>
        /// Status handshake received
        /// </summary>
        Open,

        /// <summary>
        /// Disconnecting P2P protocols.
        /// </summary>
        Disconnecting,

        /// <summary>
        /// Disconnected.
        /// </summary>
        Disconnected
    }
}
