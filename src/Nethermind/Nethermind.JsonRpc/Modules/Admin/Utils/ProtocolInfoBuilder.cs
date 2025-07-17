// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Network;
using Nethermind.JsonRpc.Modules.Admin.Models;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class ProtocolInfoBuilder
    {
        public static Dictionary<string, object> Build(Peer peer)
        {
            ArgumentNullException.ThrowIfNull(peer);

            var protocols = new Dictionary<string, object>();
            var capabilities = GetCapabilities(peer);

            // ETH protocol (always present)
            var ethVersion = ExtractProtocolVersion(capabilities, NetworkConstants.EthPrefix);
            protocols[NetworkConstants.EthPrefix] = new EthProtocolDetails { Version = ethVersion };

            // SNAP protocol (if supported)
            var snapVersion = ExtractProtocolVersion(capabilities, NetworkConstants.SNAPPrefix);
            if (snapVersion > 0)
            {
                protocols[NetworkConstants.SNAPPrefix] = new { version = snapVersion };
            }

            return protocols;
        }

        private static string[] GetCapabilities(Peer peer)
        {
            var session = peer.InSession ?? peer.OutSession;
            return session != null
                ? CapabilityExtractor.GetCapabilitiesFromSession(session).ToArray()
                : Array.Empty<string>();

        }

        private static int ExtractProtocolVersion(string[] capabilities, string protocolName)
        {
            var capability = capabilities.FirstOrDefault(c => c.StartsWith($"{protocolName}/"));
            if (string.IsNullOrEmpty(capability))
                return 0;

            var parts = capability.Split('/');
            return parts.Length == 2 && parts[0] == protocolName && int.TryParse(parts[1], out var version)
                ? version
                : 0;
        }
    }
}
