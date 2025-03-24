// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Core.Test.Modules;

public class FixedIpResolver(INetworkConfig networkConfig) : IIPResolver
{
    public IPAddress LocalIp => IPAddress.Parse(networkConfig.LocalIp!);
    public IPAddress ExternalIp => IPAddress.Parse(networkConfig.ExternalIp!);
    public Task Initialize()
    {
        return Task.CompletedTask;
    }
}
