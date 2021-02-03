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

using System.Net;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class IPResolverTests
    {
        [Test]
        public async Task Can_resolve_external_ip()
        {
            var ipResolver = new IPResolver(new NetworkConfig(), LimboLogs.Instance);
            await ipResolver.Initialize();
            var address = ipResolver.ExternalIp;
            Assert.IsNotNull(address);
        }
        
        [TestCase("99.99.99.99")]
        [TestCase("10.50.50.50")]
        public async Task Can_resolve_external_ip_with_override(string ipOverride)
        {
            INetworkConfig networkConfig = new NetworkConfig();
            networkConfig.ExternalIp = ipOverride;
            var ipResolver = new IPResolver(networkConfig, LimboLogs.Instance);
            await ipResolver.Initialize();
            var address = ipResolver.ExternalIp;
            Assert.AreEqual(IPAddress.Parse(ipOverride), address);
        }

        [Test]
        public async Task Can_resolve_internal_ip()
        {
            var ipResolver = new IPResolver(new NetworkConfig(), LimboLogs.Instance);
            await ipResolver.Initialize();
            var address = ipResolver.LocalIp;
            Assert.IsNotNull(address);
        }
        
        [Test]
        public async Task Can_resolve_local_ip_with_override()
        {
            string ipOverride = "99.99.99.99";
            INetworkConfig networkConfig = new NetworkConfig();
            networkConfig.LocalIp = ipOverride;
            var ipResolver = new IPResolver(networkConfig, LimboLogs.Instance);
            await ipResolver.Initialize();
            var address = ipResolver.LocalIp;
            Assert.AreEqual(IPAddress.Parse(ipOverride), address);
        }
    }
}
