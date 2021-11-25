//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using System.Linq;
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
