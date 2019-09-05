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

using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    public class JsonConfigProviderTests
    {
        private JsonConfigProvider _configProvider;

        [SetUp]
        public void Initialize()
        {
            var keystoreConfig = new KeyStoreConfig();
            var networkConfig = new NetworkConfig();
            var jsonRpcConfig = new JsonRpcConfig();
            var statsConfig = new StatsConfig();

            _configProvider = new JsonConfigProvider("SampleJsonConfig.cfg");
        }

        [Test]
        public void Provides_helpful_error_message_when_file_does_not_exist()
        {
            Assert.Throws<IOException>(() => _configProvider = new JsonConfigProvider("SampleJson.cfg"));
        }
        
        [Test]
        public void Can_load_config_from_file()
        {
            var keystoreConfig = _configProvider.GetConfig<IKeyStoreConfig>();
            var networkConfig = _configProvider.GetConfig<IDiscoveryConfig>();
            var jsonRpcConfig = _configProvider.GetConfig<IJsonRpcConfig>();
            var statsConfig = _configProvider.GetConfig<IStatsConfig>();

            Assert.AreEqual(100, keystoreConfig.KdfparamsDklen);
            Assert.AreEqual("test", keystoreConfig.Cipher);
          
            Assert.AreEqual(2, jsonRpcConfig.EnabledModules.Count());
            new[] { ModuleType.Eth, ModuleType.Debug }.ToList().ForEach(x =>
            {
                Assert.IsTrue(jsonRpcConfig.EnabledModules.Contains(x.ToString()));
            });

            Assert.AreEqual(4, networkConfig.Concurrency);
            Assert.AreEqual(3, statsConfig.PenalizedReputationLocalDisconnectReasons.Length);
            new[] { DisconnectReason.UnexpectedIdentity, DisconnectReason.IncompatibleP2PVersion, DisconnectReason.BreachOfProtocol }
                .ToList().ForEach(x =>
            {
                Assert.IsTrue(statsConfig.PenalizedReputationLocalDisconnectReasons.Contains(x));
            });

            NetworkNode[] nodes = NetworkNode.ParseNodes(networkConfig.Bootnodes, LimboNoErrorLogger.Instance);
            Assert.AreEqual(2, nodes.Length);

            var node1 = nodes[0];
            Assert.IsNotNull(node1);
            Assert.AreEqual("40.70.214.166", node1.Host);
            Assert.AreEqual(40303, node1.Port);

            var node2 = nodes[1];
            Assert.IsNotNull(node2);
            Assert.AreEqual("213.186.16.82", node2.Host);
            Assert.AreEqual(1345, node2.Port);
        }
    }
}
