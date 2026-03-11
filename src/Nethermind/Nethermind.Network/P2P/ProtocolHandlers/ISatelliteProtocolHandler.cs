// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.ProtocolHandlers;

/// <summary>
/// Marker interface for satellite protocols (e.g., Snap, NodeData) that attach to existing sync peers.
/// Satellite protocols are initialized via InitSatelliteProtocol().
/// </summary>
public interface ISatelliteProtocolHandler : IProtocolHandler
{
}
