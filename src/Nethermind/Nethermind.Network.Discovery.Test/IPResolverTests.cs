// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class IPResolverTests
    {
        [Test]
        public async Task Can_resolve_external_ip()
        {
            IPResolver ipResolver = new(new NetworkConfig(), LimboLogs.Instance);
            await ipResolver.Initialize();
            IPAddress address = ipResolver.ExternalIp;
            Assert.IsNotNull(address);
        }

        [TestCase("99.99.99.99")]
        [TestCase("10.50.50.50")]
        public async Task Can_resolve_external_ip_with_override(string ipOverride)
        {
            INetworkConfig networkConfig = new NetworkConfig();
            networkConfig.ExternalIp = ipOverride;
            IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);
            await ipResolver.Initialize();
            IPAddress address = ipResolver.ExternalIp;
            Assert.That(address, Is.EqualTo(IPAddress.Parse(ipOverride)));
        }

        [Test]
        public async Task Can_resolve_internal_ip()
        {
            IPResolver ipResolver = new(new NetworkConfig(), LimboLogs.Instance);
            await ipResolver.Initialize();
            IPAddress address = ipResolver.LocalIp;
            Assert.IsNotNull(address);
        }

        [Test]
        public async Task Can_resolve_local_ip_with_override()
        {
            string ipOverride = "99.99.99.99";
            INetworkConfig networkConfig = new NetworkConfig();
            networkConfig.LocalIp = ipOverride;
            IPResolver ipResolver = new(networkConfig, LimboLogs.Instance);
            await ipResolver.Initialize();
            IPAddress address = ipResolver.LocalIp;
            Assert.That(address, Is.EqualTo(IPAddress.Parse(ipOverride)));
        }
    }
}
