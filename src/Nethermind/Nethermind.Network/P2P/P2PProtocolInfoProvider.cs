// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public static class P2PProtocolInfoProvider
    {
        public static int GetHighestVersionOfEthProtocol()
        {
            int highestVersion = 0;
            foreach (Capability ethProtocol in P2PProtocolHandler.DefaultCapabilities)
            {
                if (ethProtocol.ProtocolCode == Protocol.Eth && highestVersion < ethProtocol.Version)
                    highestVersion = ethProtocol.Version;
            }

            return highestVersion;
        }

        public static string DefaultCapabilitiesToString()
        {
            IEnumerable<string> capabilities = P2PProtocolHandler.DefaultCapabilities
                .OrderBy(x => x.ProtocolCode).ThenByDescending(x => x.Version)
                .Select(x => $"{x.ProtocolCode}/{x.Version}");
            return string.Join(",", capabilities);
        }
    }
}
