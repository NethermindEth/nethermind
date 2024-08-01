// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;

namespace Nethermind.Network.IP
{
    class EnvironmentVariableIPSource : IIPSource
    {
        public Task<(bool, IPAddress)> TryGetIP()
        {
            string externalIpSetInEnv = Environment.GetEnvironmentVariable("NETHERMIND_ENODE_IPADDRESS");
            bool success = IPAddress.TryParse(externalIpSetInEnv, out IPAddress ipAddress);
            return Task.FromResult((success, ipAddress));
        }
    }
}
