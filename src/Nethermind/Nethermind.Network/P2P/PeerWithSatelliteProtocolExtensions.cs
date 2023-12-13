// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.Network.P2P
{
    public static class PeerWithSatelliteProtocolExtensions
    {
        public static void RegisterSatelliteProtocol<T>(this IPeerWithSatelliteProtocol peerWithSatelliteProtocol, T handler)
            where T : ProtocolHandlerBase
        {
            peerWithSatelliteProtocol.RegisterSatelliteProtocol(handler.ProtocolCode, handler);
        }
    }
}
