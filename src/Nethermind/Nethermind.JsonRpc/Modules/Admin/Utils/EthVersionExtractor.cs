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
                var ethCapability = capabilities.FirstOrDefault(c => c.StartsWith(NetworkConstants.EthProtocolPrefix));
                return ParseEthVersion(ethCapability);
            }

            return 0; // No session = no version info
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