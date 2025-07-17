// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class CapabilityExtractor
    {
        public static string[] ExtractCapabilities(Peer peer)
        {
            var capabilities = new List<string>(capacity: 8);

            ISession? session = peer.InSession ?? peer.OutSession;
            if (session != null)
            {
                capabilities.AddRange(GetCapabilitiesFromSession(session));
            }

            // Fallback to EthDetails parsing if no capabilities found
            if (capabilities.Count == 0 && !string.IsNullOrEmpty(peer.Node.EthDetails))
            {
                capabilities.Add(peer.Node.EthDetails);
            }

            return capabilities.ToArray();
        }

        public static IEnumerable<string> GetCapabilitiesFromSession(ISession session)
        {
            if (session.TryGetProtocolHandler("p2p", out IProtocolHandler? p2pHandler) &&
                p2pHandler is IP2PProtocolHandler p2pProtocol)
            {
                return p2pProtocol.GetCapabilitiesForAdmin();
            }

            return Enumerable.Empty<string>();
        }
    }
}