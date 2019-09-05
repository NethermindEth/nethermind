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
using System.Net;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.EthStats;
using Nethermind.Grpc;
using Nethermind.JsonRpc;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using Nethermind.Network.Config;
using Nethermind.PubSub.Kafka;
using Nethermind.Runner.Config;
using Nethermind.Stats;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    public class ConfigFilesTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("spaceneth.cfg", true)]
        public void Mining_defaults_are_correct(string configFile, bool defaultValue = false)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IInitConfig config = configProvider.GetConfig<IInitConfig>();
            Assert.AreEqual(config.IsMining, defaultValue);
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("spaceneth.cfg")]
        [TestCase("ndm_consumer_goerli.cfg")]
        [TestCase("ndm_consumer_local.cfg")]
        public void Required_config_files_exist(string configFile)
        {
            var configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
            Assert.True(File.Exists(configPath));
        }

        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        public void Eth_stats_disabled_by_default(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IEthStatsConfig config = configProvider.GetConfig<IEthStatsConfig>();
            Assert.AreEqual(config.Enabled, false);
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        public void Grpc_disabled_by_default(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IGrpcConfig config = configProvider.GetConfig<IGrpcConfig>();
            Assert.AreEqual(false, config.Enabled);
            Assert.AreEqual(false, config.ProducerEnabled);
        }
        
        [TestCase("ndm_consumer_goerli.cfg")]
        [TestCase("ndm_consumer_local.cfg")]
        public void Grpc_enabled_for_ndm(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IGrpcConfig config = configProvider.GetConfig<IGrpcConfig>();
            Assert.AreEqual(true, config.Enabled);
            Assert.AreEqual(false, config.ProducerEnabled);
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        public void Ndm_disabled_by_default(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            INdmConfig config = configProvider.GetConfig<INdmConfig>();
            Assert.AreEqual(config.Enabled, false);
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        public void Metrics_disabled_by_default(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IMetricsConfig config = configProvider.GetConfig<IMetricsConfig>();
            Assert.AreEqual(config.Enabled, false);
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        public void Default_ports_are_correct(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
            Assert.AreEqual(30303, networkConfig.DiscoveryPort, nameof(networkConfig.DiscoveryPort));
            Assert.AreEqual(30303, networkConfig.P2PPort, nameof(networkConfig.P2PPort));
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        public void Json_default_are_correct(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();
            Assert.AreEqual(8545, jsonRpcConfig.Port, nameof(jsonRpcConfig.Port));
            Assert.AreEqual("127.0.0.1", jsonRpcConfig.Host, nameof(jsonRpcConfig.Host));
            Assert.AreEqual(false, jsonRpcConfig.Enabled, nameof(jsonRpcConfig.Enabled));
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg")]
        public void Kafka_disabled_by_default(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IKafkaConfig kafkaConfig = configProvider.GetConfig<IKafkaConfig>();
            Assert.AreEqual(false, kafkaConfig.Enabled, nameof(kafkaConfig.Enabled));
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg", true)]
        [TestCase("mainnet.cfg", true)]
        [TestCase("sokol.cfg")]
        [TestCase("poacore.cfg", true)]
        public void Basic_configs_are_as_expected(string configFile, bool isProduction = false)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            
            Assert.True(initConfig.DiscoveryEnabled, nameof(initConfig.DiscoveryEnabled));
            Assert.True(initConfig.ProcessingEnabled, nameof(initConfig.ProcessingEnabled));
            Assert.True(initConfig.PeerManagerEnabled, nameof(initConfig.PeerManagerEnabled));
            Assert.True(initConfig.SynchronizationEnabled, nameof(initConfig.SynchronizationEnabled));
            Assert.False(initConfig.WebSocketsEnabled, nameof(initConfig.WebSocketsEnabled));
            if (isProduction)
            {
                Assert.False(initConfig.EnableUnsecuredDevWallet, nameof(initConfig.EnableUnsecuredDevWallet));
            }

            Assert.False(initConfig.KeepDevWalletInMemory, nameof(initConfig.KeepDevWalletInMemory));
            Assert.False(initConfig.IsMining, nameof(initConfig.IsMining));
            Assert.True(initConfig.StoreReceipts, nameof(initConfig.StoreReceipts));
            Assert.False(initConfig.EnableRc7Fix, nameof(initConfig.EnableRc7Fix));
            Assert.AreEqual(configFile.Replace("cfg", "logs.txt"), initConfig.LogFileName, nameof(initConfig.LogFileName));
            Assert.False(initConfig.StoreTraces, nameof(initConfig.StoreTraces));
            Assert.AreEqual("chainspec", initConfig.ChainSpecFormat, nameof(initConfig.ChainSpecFormat));
            Assert.False(initConfig.StoreTraces, nameof(initConfig.StoreTraces));
        }

        private static ConfigProvider GetConfigProviderFromFile(string configFile)
        {
            ConfigProvider configProvider = new ConfigProvider();
            var configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
            configProvider.AddSource(new JsonConfigSource(configPath));
            return configProvider;
        }
    }
}