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

using System.IO;
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
        private JsonConfigProvider _configProvider;

        [SetUp]
        public void Initialize()
        {
            var keystoreConfig = new KeyStoreConfig();
            var networkConfig = new NetworkConfig();
            var jsonRpcConfig = new JsonRpcConfig();
            var statsConfig = StatsParameters.Instance;

            _configProvider = new JsonConfigProvider("SampleJson/SampleJsonConfig.cfg");
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

            Assert.AreEqual(100, keystoreConfig.KdfparamsDklen);
            Assert.AreEqual("test", keystoreConfig.Cipher);
          
            Assert.AreEqual(2, jsonRpcConfig.EnabledModules.Count());
            new[] { ModuleType.Eth, ModuleType.Debug }.ToList().ForEach(x =>
            {
                Assert.IsTrue(jsonRpcConfig.EnabledModules.Contains(x.ToString()));
            });

            Assert.AreEqual(4, networkConfig.Concurrency);
        }
        
        [Test]
        public void Can_load_raw_value()
        {
            Assert.AreEqual("100", _configProvider.GetRawValue("KeyStoreConfig", "KdfparamsDklen"));
        }
    }
}
