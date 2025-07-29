// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;

using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public static class P2PProtocolInfoProvider
    {
        public static int GetHighestVersionOfEthProtocol()
        {
            int highestVersion = 0;
            foreach (Capability ethProtocol in ProtocolsManager.DefaultCapabilities)
            {
                if (ethProtocol.ProtocolCode == Protocol.Eth && highestVersion < ethProtocol.Version)
                    highestVersion = ethProtocol.Version;
            }

            return highestVersion;
        }

        public static string DefaultCapabilitiesToString()
        {
            IEnumerable<string> capabilities = ProtocolsManager.DefaultCapabilities
                .OrderBy(static x => x.ProtocolCode).ThenByDescending(static x => x.Version)
                .Select(static x => $"{x.ProtocolCode}/{x.Version}");
            return string.Join(",", capabilities);
        }
    }
}
