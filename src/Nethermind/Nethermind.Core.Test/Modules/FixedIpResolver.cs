// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Core.Test.Modules;

public class FixedIpResolver(INetworkConfig networkConfig) : IIPResolver
{
    private readonly IIPResolver.NethermindIp _addresses = new(
        networkConfig.LocalIp is null ? IPAddress.Loopback : IPAddress.Parse(networkConfig.LocalIp),
        networkConfig.ExternalIp is null ? IPAddress.None : IPAddress.Parse(networkConfig.ExternalIp),
        networkConfig.ExternalIpV4 is null ? null : IPAddress.Parse(networkConfig.ExternalIpV4),
        networkConfig.ExternalIpV6 is null ? null : IPAddress.Parse(networkConfig.ExternalIpV6));

    /// <inheritdoc/>
    /// <remarks>The immutable snapshot is already complete and never refreshes.</remarks>
    public event EventHandler<IIPResolver.NethermindIp>? Resolved
    {
        add { }
        remove { }
    }

    public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) => new(_addresses);
}
