// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            var session = peer.InSession ?? peer.OutSession;
            return GetCapabilitiesFromSession(session).ToArray();
        }

        public static IEnumerable<string> GetCapabilitiesFromSession(ISession? session)
        {
            if (session?.TryGetProtocolHandler(NetworkConstants.P2PPrefix, out IProtocolHandler? handler) == true &&
                handler is IP2PProtocolHandler p2pHandler)
            {
                return p2pHandler.GetCapabilitiesForAdmin() ?? Enumerable.Empty<string>();
            }
            
            return Enumerable.Empty<string>();
        }
    }
}