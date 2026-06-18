// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Core.Test.Modules;

public class FixedIpResolver(INetworkConfig networkConfig) : IIPResolver
{
    public ValueTask<NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
        new(new NethermindIp(
            networkConfig.LocalIp is null ? IPAddress.Loopback : IPAddress.Parse(networkConfig.LocalIp),
            networkConfig.ExternalIp is null ? IPAddress.None : IPAddress.Parse(networkConfig.ExternalIp)));
}
