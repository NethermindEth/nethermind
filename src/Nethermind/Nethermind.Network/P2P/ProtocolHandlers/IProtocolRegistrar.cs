// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.ProtocolHandlers;

/// <summary>
/// Receives protocol handler registrations via double dispatch.
/// Each handler calls the appropriate overload from its <see cref="IProtocolHandler.RegisterWith"/> method,
/// and compile-time overload resolution selects the correct registration path.
/// </summary>
public interface IProtocolRegistrar
{
    /// <summary>
    /// Registers a satellite subprotocol (e.g., Snap, NodeData) — attaches to an existing sync peer.
    /// </summary>
    void Register(ISession session, ProtocolHandlerBase handler);

    /// <summary>
    /// Registers P2P base protocol — sets up ping, capabilities, snappy, and discovery.
    /// </summary>
    void Register(ISession session, P2PProtocolHandler handler);

    /// <summary>
    /// Registers a sync peer protocol (e.g., ETH) — wires into sync pool, tx pool, and peer storage.
    /// </summary>
    void Register(ISession session, SyncPeerProtocolHandlerBase handler);
}
