// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    /// <summary>
    /// Custom factory interface for creating Eth protocol handlers for non-standard protocol versions.
    /// This allows plugins (like XDC) to register handlers for protocol versions not natively supported.
    /// </summary>
    public interface ICustomEthProtocolFactory
    {
        /// <summary>
        /// Checks if this factory can handle the specified protocol version.
        /// </summary>
        /// <param name="version">The Eth protocol version</param>
        /// <returns>True if this factory can create a handler for the version</returns>
        bool CanHandle(int version);

        /// <summary>
        /// Creates a protocol handler for the specified version.
        /// </summary>
        /// <param name="session">The peer session</param>
        /// <param name="version">The protocol version</param>
        /// <returns>A protocol handler, or null if the version cannot be handled</returns>
        SyncPeerProtocolHandlerBase? Create(ISession session, int version);
    }
}
