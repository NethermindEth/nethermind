// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Network.P2P;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class EthVersionExtractor
    {
        public static int ExtractEthVersion(Peer peer)
        {
            // Try to get version from capabilities first
            ISession? session = peer.InSession ?? peer.OutSession;
            if (session != null)
            {
                int versionFromCapabilities = GetVersionFromCapabilities(session);
                if (versionFromCapabilities > 0)
                {
                    return versionFromCapabilities;
                }
            }

            // Fallback to EthDetails
            return GetVersionFromEthDetails(peer.Node.EthDetails);
        }

        private static int GetVersionFromCapabilities(ISession session)
        {
            IEnumerable<string> capabilities = CapabilityExtractor.GetCapabilitiesFromSession(session);
            string? ethCapability = capabilities.FirstOrDefault(c => c.StartsWith(NetworkConstants.EthProtocolPrefix));
            
            return ParseEthVersion(ethCapability);
        }

        private static int GetVersionFromEthDetails(string? ethDetails)
        {
            return ParseEthVersion(ethDetails);
        }

        private static int ParseEthVersion(string? ethString)
        {
            if (string.IsNullOrEmpty(ethString) || !ethString.StartsWith(NetworkConstants.EthProtocolPrefix))
            {
                return 0;
            }

            string versionString = ethString.Substring(NetworkConstants.EthProtocolPrefix.Length);
            return int.TryParse(versionString, out int version) ? version : 0;
        }
    }
}