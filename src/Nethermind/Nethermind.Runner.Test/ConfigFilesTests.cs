//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Net;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.EthStats;
using Nethermind.Grpc;
using Nethermind.JsonRpc;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using Nethermind.Network.Config;
using Nethermind.PubSub.Kafka;
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

        [TestCase("ropsten_archive.cfg", false, false)]
        [TestCase("ropsten.cfg", true, false)]
        [TestCase("rinkeby_archive.cfg", false, false)]
        [TestCase("rinkeby.cfg", true, true)]
        [TestCase("goerli_archive.cfg", false, false)]
        [TestCase("goerli.cfg", true, true)]
        [TestCase("mainnet_archive.cfg", false, false)]
        [TestCase("mainnet.cfg", true, true)]
        [TestCase("sokol.cfg", true, true)]
        [TestCase("sokol_archive.cfg", false, false)]
        [TestCase("sokol_validator.cfg", true, true)]
        [TestCase("sokol_fastsync.cfg", true, false)]        
        [TestCase("poacore.cfg", true, true)]
        [TestCase("poacore_archive.cfg", false, false)]
        [TestCase("poacore_validator.cfg", true, true)]
        [TestCase("xdai.cfg", true, true)]
        [TestCase("xdai_archive.cfg", false, false)]
        [TestCase("xdai_validator.cfg", true, true)]
        [TestCase("spaceneth.cfg", false, false)]
        [TestCase("volta.cfg", true, false)]
        [TestCase("volta_archive.cfg", false, false)]
        public void Sync_defaults_are_correct(string configFile, bool fastSyncEnabled, bool fastBlocksEnabled)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            ISyncConfig config = configProvider.GetConfig<ISyncConfig>();
            Assert.AreEqual(fastSyncEnabled, config.FastSync, "fast sync");
            Assert.AreEqual(fastBlocksEnabled, config.FastBlocks, "fast blocks");
            Assert.AreEqual(false, config.BeamSyncEnabled);
        }
        
        [TestCase("ropsten_archive.cfg", true)]
        [TestCase("ropsten.cfg", true)]
        [TestCase("rinkeby_archive.cfg", true)]
        [TestCase("rinkeby.cfg", true)]
        [TestCase("goerli_archive.cfg", true)]
        [TestCase("goerli.cfg", true)]
        [TestCase("mainnet_archive.cfg", true)]
        [TestCase("mainnet.cfg", true)]
        [TestCase("sokol.cfg", true)]
        [TestCase("sokol_archive.cfg", true)]
        [TestCase("sokol_validator.cfg", true)]
        [TestCase("sokol_fastsync.cfg", true)]        
        [TestCase("poacore.cfg", true)]
        [TestCase("poacore_archive.cfg", true)]
        [TestCase("poacore_validator.cfg", true)]
        [TestCase("xdai.cfg", true)]
        [TestCase("xdai_archive.cfg", true)]
        [TestCase("xdai_validator.cfg", true)]
        [TestCase("spaceneth.cfg", false)]
        [TestCase("volta.cfg", true)]
        [TestCase("volta_archive.cfg", true)]
        [TestCase("ndm_consumer_goerli.cfg", true)]
        [TestCase("ndm_consumer_local.cfg", true)]
        [TestCase("ndm_consumer_mainnet_proxy.cfg", false)]
        [TestCase("ndm_consumer_ropsten.cfg", true)]
        [TestCase("ndm_consumer_ropsten_proxy.cfg", false)]
        public void Sync_is_disabled_when_needed(string configFile, bool isSyncEnabled)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            ISyncConfig config = configProvider.GetConfig<ISyncConfig>();
            Assert.AreEqual(isSyncEnabled, config.SynchronizationEnabled);
        }
        
        [TestCase("ropsten_archive.cfg", "ws://ropsten-stats.parity.io/api")]
        [TestCase("ropsten.cfg", "ws://ropsten-stats.parity.io/api")]
        [TestCase("rinkeby_archive.cfg", "ws://localhost:3000/api")]
        [TestCase("rinkeby.cfg", "ws://localhost:3000/api")]
        [TestCase("goerli_archive.cfg", "wss://stats.goerli.net/api")]
        [TestCase("goerli.cfg", "wss://stats.goerli.net/api")]
        [TestCase("mainnet_archive.cfg", "wss://ethstats.net/api")]
        [TestCase("mainnet.cfg", "wss://ethstats.net/api")]
        [TestCase("sokol.cfg", "ws://localhost:3000/api")]
        [TestCase("sokol_archive.cfg", "ws://localhost:3000/api")]
        [TestCase("sokol_validator.cfg", "ws://localhost:3000/api")]
        [TestCase("sokol_fastsync.cfg", "ws://localhost:3000/api")]        
        [TestCase("poacore.cfg", "ws://localhost:3000/api")]
        [TestCase("poacore_archive.cfg", "ws://localhost:3000/api")]
        [TestCase("poacore_validator.cfg", "ws://localhost:3000/api")]
        [TestCase("xdai.cfg", "ws://localhost:3000/api")]
        [TestCase("xdai_archive.cfg", "ws://localhost:3000/api")]
        [TestCase("xdai_validator.cfg", "ws://localhost:3000/api")]
        [TestCase("spaceneth.cfg", "ws://localhost:3000/api")]
        [TestCase("volta.cfg", "ws://localhost:3000/api")]
        [TestCase("volta_archive.cfg", "ws://localhost:3000/api")]
        public void Ethstats_values_are_correct(string configFile, string host)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IEthStatsConfig config = configProvider.GetConfig<IEthStatsConfig>();
            Assert.AreEqual(host, config.Server);
        }
        
        [TestCase("ropsten_archive.cfg", true)]
        [TestCase("ropsten.cfg", true)]
        [TestCase("rinkeby_archive.cfg", true)]
        [TestCase("rinkeby.cfg", true)]
        [TestCase("goerli_archive.cfg", true)]
        [TestCase("goerli.cfg", false)]
        [TestCase("mainnet_archive.cfg", true)]
        [TestCase("mainnet.cfg", true)]
        [TestCase("sokol.cfg", false)]
        [TestCase("sokol_archive.cfg", false)]
        [TestCase("sokol_validator.cfg", false)]
        [TestCase("sokol_fastsync.cfg", false)]
        [TestCase("poacore.cfg", false)]
        [TestCase("poacore_archive.cfg", false)]
        [TestCase("poacore_validator.cfg", false)]
        [TestCase("xdai.cfg", false)]
        [TestCase("xdai_archive.cfg", false)]
        [TestCase("xdai_validator.cfg", false)]
        [TestCase("spaceneth.cfg", true)]
        [TestCase("volta.cfg", false)]
        [TestCase("volta_archive.cfg", false)]
        public void Geth_limits_configs_are_correct(string configFile, bool useGethLimitsInFastSync)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            ISyncConfig config = configProvider.GetConfig<ISyncConfig>();
            Assert.AreEqual( useGethLimitsInFastSync, config.UseGethLimitsInFastBlocks);
        }
        
        [TestCase("ropsten_archive.cfg", "0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d")]
        [TestCase("ropsten.cfg", "0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d")]
        [TestCase("rinkeby_archive.cfg", "0x6341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177")]
        [TestCase("rinkeby.cfg", "0x6341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177")]
        [TestCase("goerli_archive.cfg", "0xbf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1a")]
        [TestCase("goerli.cfg", "0xbf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1a")]
        [TestCase("mainnet_archive.cfg", "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3")]
        [TestCase("mainnet.cfg", "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3")]
        [TestCase("sokol.cfg", "0x5b28c1bfd3a15230c9a46b399cd0f9a6920d432e85381cc6a140b06e8410112f")]
        [TestCase("sokol_archive.cfg", "0x5b28c1bfd3a15230c9a46b399cd0f9a6920d432e85381cc6a140b06e8410112f")]
        [TestCase("sokol_validator.cfg", "0x5b28c1bfd3a15230c9a46b399cd0f9a6920d432e85381cc6a140b06e8410112f")]
        [TestCase("sokol_fastsync.cfg", "0x5b28c1bfd3a15230c9a46b399cd0f9a6920d432e85381cc6a140b06e8410112f")]
        [TestCase("poacore.cfg", "0x39f02c003dde5b073b3f6e1700fc0b84b4877f6839bb23edadd3d2d82a488634")]
        [TestCase("poacore_archive.cfg", "0x39f02c003dde5b073b3f6e1700fc0b84b4877f6839bb23edadd3d2d82a488634")]
        [TestCase("poacore_validator.cfg", "0x39f02c003dde5b073b3f6e1700fc0b84b4877f6839bb23edadd3d2d82a488634")]
        [TestCase("xdai.cfg", "0x4f1dd23188aab3a76b463e4af801b52b1248ef073c648cbdc4c9333d3da79756")]
        [TestCase("xdai_archive.cfg", "0x4f1dd23188aab3a76b463e4af801b52b1248ef073c648cbdc4c9333d3da79756")]
        [TestCase("xdai_validator.cfg", "0x4f1dd23188aab3a76b463e4af801b52b1248ef073c648cbdc4c9333d3da79756")]
        [TestCase("volta.cfg", "0xebd8b413ca7b7f84a8dd20d17519ce2b01954c74d94a0a739a3e416abe0e43e5")]
        [TestCase("volta_archive.cfg", "0xebd8b413ca7b7f84a8dd20d17519ce2b01954c74d94a0a739a3e416abe0e43e5")]
        public void Genesis_hash_is_correct(string configFile, string genesisHash)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IInitConfig config = configProvider.GetConfig<IInitConfig>();
            Assert.AreEqual(config.GenesisHash, genesisHash);
        }
        
        [TestCase("ropsten_archive.cfg")]
        [TestCase("ropsten.cfg")]
        [TestCase("rinkeby_archive.cfg")]
        [TestCase("rinkeby.cfg")]
        [TestCase("goerli_archive.cfg")]
        [TestCase("goerli.cfg")]
        [TestCase("mainnet_archive.cfg")]
        [TestCase("mainnet.cfg")]
        [TestCase("spaceneth.cfg", true)]
        [TestCase("sokol.cfg")]
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg", true)]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg", true)]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg", true)]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg")]
        [TestCase("spaceneth.cfg")]
        [TestCase("ndm_consumer_goerli.cfg")]
        [TestCase("ndm_consumer_local.cfg")]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg")]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg")]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg")]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg")]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg")]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
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
        
        [TestCase("ndm_consumer_local.cfg")]
        public void IsMining_enabled_for_ndm_consumer_local(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IInitConfig config = configProvider.GetConfig<IInitConfig>();
            Assert.AreEqual(true, config.IsMining);
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg")]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg")]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg")]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg")]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg")]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg")]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
        public void Network_defaults_are_correct(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
            Assert.AreEqual(30303, networkConfig.DiscoveryPort, nameof(networkConfig.DiscoveryPort));
            Assert.AreEqual(30303, networkConfig.P2PPort, nameof(networkConfig.P2PPort));
            Assert.Null(networkConfig.ExternalIp, nameof(networkConfig.ExternalIp));
            Assert.Null(networkConfig.LocalIp, nameof(networkConfig.LocalIp));
            Assert.AreEqual(50, networkConfig.ActivePeersMaxCount, 50);
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg")]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg")]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_validator.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg")]
        [TestCase("poacore_archive.cfg")]
        [TestCase("poacore_validator.cfg")]
        [TestCase("xdai.cfg")]
        [TestCase("xdai_archive.cfg")]
        [TestCase("xdai_validator.cfg")]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
        public void Kafka_disabled_by_default(string configFile)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IKafkaConfig kafkaConfig = configProvider.GetConfig<IKafkaConfig>();
            Assert.AreEqual(false, kafkaConfig.Enabled, nameof(kafkaConfig.Enabled));
        }
        
        [TestCase("ropsten.cfg", true, true)]
        [TestCase("rinkeby.cfg", true, true)]
        [TestCase("goerli.cfg", true, true)]
        [TestCase("mainnet.cfg", false, false)]
        [TestCase("sokol.cfg", true, true)]
        [TestCase("sokol_validator.cfg", false, false)]
        [TestCase("sokol_fastsync.cfg", true, true)]
        [TestCase("poacore.cfg", true, true)]
        [TestCase("poacore_validator.cfg", false, false)]
        [TestCase("xdai.cfg", true, true)]
        [TestCase("xdai_validator.cfg", false, false)]
        [TestCase("volta.cfg", true, true)]
        public void Fast_sync_settings_as_expected(string configFile, bool downloadBodies, bool downloadsReceipts)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
            Assert.AreEqual(downloadBodies, syncConfig.DownloadBodiesInFastSync, nameof(syncConfig.DownloadBodiesInFastSync));
            Assert.AreEqual(downloadsReceipts, syncConfig.DownloadReceiptsInFastSync, nameof(syncConfig.DownloadReceiptsInFastSync));
        }
        
        [TestCase("ropsten.cfg", false)]
        [TestCase("rinkeby.cfg", false)]
        [TestCase("goerli.cfg", false)]
        [TestCase("mainnet.cfg", false)]
        [TestCase("sokol.cfg", true)]
        [TestCase("sokol_validator.cfg", true)]
        [TestCase("sokol_fastsync.cfg", true)]
        [TestCase("poacore.cfg", true)]
        [TestCase("poacore_validator.cfg", true)]
        [TestCase("xdai.cfg", true)]
        [TestCase("xdai_validator.cfg", true)]
        [TestCase("volta.cfg", false)]
        public void Stays_on_full_sync(string configFile, bool stickToFullSyncAfterFastSync)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
            if (stickToFullSyncAfterFastSync)
            {
                syncConfig.FastSyncCatchUpHeightDelta.Should().BeGreaterOrEqualTo(1000000000);
            }
            else
            {
                syncConfig.FastSyncCatchUpHeightDelta.Should().Be(null);
            }
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
        [TestCase("sokol_archive.cfg")]
        [TestCase("sokol_fastsync.cfg")]
        [TestCase("poacore.cfg", true)]
        [TestCase("poacore_archive.cfg", true)]
        [TestCase("xdai.cfg", true)]
        [TestCase("xdai_archive.cfg", true)]
        [TestCase("volta.cfg")]
        [TestCase("volta_archive.cfg")]
        public void Basic_configs_are_as_expected(string configFile, bool isProduction = false)
        {
            ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
            
            Assert.True(initConfig.DiscoveryEnabled, nameof(initConfig.DiscoveryEnabled));
            Assert.True(initConfig.ProcessingEnabled, nameof(initConfig.ProcessingEnabled));
            Assert.True(initConfig.PeerManagerEnabled, nameof(initConfig.PeerManagerEnabled));
            Assert.True(syncConfig.SynchronizationEnabled, nameof(syncConfig.SynchronizationEnabled));
            Assert.False(initConfig.WebSocketsEnabled, nameof(initConfig.WebSocketsEnabled));
            if (isProduction)
            {
                Assert.False(initConfig.EnableUnsecuredDevWallet, nameof(initConfig.EnableUnsecuredDevWallet));
            }

            Assert.False(initConfig.KeepDevWalletInMemory, nameof(initConfig.KeepDevWalletInMemory));
            Assert.False(initConfig.IsMining, nameof(initConfig.IsMining));
            Assert.True(initConfig.StoreReceipts, nameof(initConfig.StoreReceipts));

            Assert.AreEqual(configFile.Replace("cfg", "logs.txt"), initConfig.LogFileName, nameof(initConfig.LogFileName));
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