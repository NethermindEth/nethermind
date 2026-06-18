// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.All)]
public class IPResolverTests
{
    [Test]
    public async Task Can_resolve_ip_without_override()
    {
        IPResolver ipResolver = new(new NetworkConfig(), LimboLogs.Instance);
        NethermindIp ip = await ipResolver.Resolve();
        Assert.That(ip.LocalIp, Is.Not.Null);
        Assert.That(ip.ExternalIp, Is.Not.Null);
    }

    [TestCase("99.99.99.99")]
    [TestCase("10.50.50.50")]
    public async Task Can_resolve_external_ip_with_override(string ipOverride)
    {
        INetworkConfig networkConfig = new NetworkConfig { ExternalIp = ipOverride };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);
        NethermindIp ip = await ipResolver.Resolve();
        Assert.That(ip.ExternalIp, Is.EqualTo(IPAddress.Parse(ipOverride)));
    }

    [Test]
    public async Task Can_resolve_local_ip_with_override()
    {
        const string ipOverride = "99.99.99.99";
        INetworkConfig networkConfig = new NetworkConfig { LocalIp = ipOverride };
        IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);
        NethermindIp ip = await ipResolver.Resolve();
        Assert.That(ip.LocalIp, Is.EqualTo(IPAddress.Parse(ipOverride)));
    }
}
