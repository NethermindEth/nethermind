// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Network;
using Nethermind.JsonRpc.Modules.Admin.Models;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class ProtocolInfoBuilder
    {
        public static Dictionary<string, object> Build(Peer peer)
        {
            var protocols = new Dictionary<string, object>();

            EthProtocolDetails ethInfo = CreateEthProtocolDetails(peer);
            protocols[NetworkConstants.EthProtocolPrefix] = ethInfo;

            return protocols;
        }

        private static EthProtocolDetails CreateEthProtocolDetails(Peer peer)
        {
            var ethInfo = new EthProtocolDetails();

            int version = EthVersionExtractor.ExtractEthVersion(peer);
            if (version > 0)
            {
                ethInfo.Version = version;
            }

            return ethInfo;
        }
    }
}
