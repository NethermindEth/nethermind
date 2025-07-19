// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Network.P2P;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class EthVersionExtractor
    {
        public static int ExtractEthVersion(Peer peer)
        {
            var session = peer.InSession ?? peer.OutSession;
            if (session != null)
            {
                var capabilities = CapabilityExtractor.GetCapabilitiesFromSession(session);
                var ethCapability = capabilities.FirstOrDefault(c => c.StartsWith(NetworkConstants.EthPrefix + "/"));
                return ParseEthVersion(ethCapability);
            }

            return 0; // No session = no version info
        }

        private static int ParseEthVersion(string? ethString)
        {
            if (string.IsNullOrEmpty(ethString))
            {
                return 0;
            }

            // Split "eth/68" into ["eth", "68"]
            var parts = ethString.Split('/');
            if (parts.Length == 2 && parts[0] == NetworkConstants.EthPrefix)
            {
                return int.TryParse(parts[1], out int version) ? version : 0;
            }

            return 0;
        }
    }
}
