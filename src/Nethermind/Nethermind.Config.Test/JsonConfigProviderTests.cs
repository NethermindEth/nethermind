/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Linq;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    public class JsonConfigProviderTests
    {
        private JsonConfigSource _configSource;

        [SetUp]
        public void Initialize()
        {
            var keystoreConfig = new KeystoreConfig();
            var networkConfig = new NetworkConfig();
            var jsonRpcConfig = new JsonRpcConfig();
            var statsConfig = new StatsConfig();

            _configSource = new JsonConfigSource();
        }

        [Test]
        public void TestLoadJsonConfig()
        {
            _configSource.LoadJsonConfig("SampleJsonConfig.json");

            var keystoreConfig = _configSource.GetConfig<IKeystoreConfig>();
            var networkConfig = _configSource.GetConfig<INetworkConfig>();
            var jsonRpcConfig = _configSource.GetConfig<IJsonRpcConfig>();
            var statsConfig = _configSource.GetConfig<IStatsConfig>();

            Assert.AreEqual(100, keystoreConfig.KdfparamsDklen);
            Assert.AreEqual("test", keystoreConfig.Cipher);
          
            Assert.AreEqual(2, jsonRpcConfig.EnabledModules.Count());
            new[] { ModuleType.Eth, ModuleType.Debug }.ToList().ForEach(x =>
            {
                Assert.IsTrue(jsonRpcConfig.EnabledModules.Contains(x));
            });

            Assert.AreEqual(4, networkConfig.Concurrency);
            Assert.AreEqual(3, statsConfig.PenalizedReputationLocalDisconnectReasons.Length);
            new[] { DisconnectReason.UnexpectedIdentity, DisconnectReason.IncompatibleP2PVersion, DisconnectReason.BreachOfProtocol }
                .ToList().ForEach(x =>
            {
                Assert.IsTrue(statsConfig.PenalizedReputationLocalDisconnectReasons.Contains(x));
            });
            Assert.AreEqual(2, networkConfig.BootNodes.Length);

            var node1 = networkConfig.BootNodes.FirstOrDefault(x => x.NodeId == "testNodeId");
            Assert.IsNotNull(node1);
            Assert.AreEqual("testHist", node1.Host);
            Assert.AreEqual(43, node1.Port);

            var node2 = networkConfig.BootNodes.FirstOrDefault(x => x.NodeId == "testNodeId2");
            Assert.IsNotNull(node2);
            Assert.AreEqual("testHist2", node2.Host);
            Assert.AreEqual(44, node2.Port);
        }
    }
}
