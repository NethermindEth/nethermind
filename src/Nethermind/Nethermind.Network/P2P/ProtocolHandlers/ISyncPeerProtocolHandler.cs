// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.ProtocolHandlers;

/// <summary>
/// Marker interface for sync peer protocols (e.g., ETH) that register with sync pool and tx pool.
/// Sync peer protocols are initialized via InitSyncPeerProtocol().
/// </summary>
public interface ISyncPeerProtocolHandler : IProtocolHandler
{
}
