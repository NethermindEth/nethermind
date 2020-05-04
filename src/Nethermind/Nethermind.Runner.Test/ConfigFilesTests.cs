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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.EthStats;
using Nethermind.Grpc;
using Nethermind.JsonRpc;
using Nethermind.Monitoring.Config;
using Nethermind.Network.Config;
using Nethermind.PubSub.Kafka;
using Nethermind.Store.Bloom;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ConfigFilesTests
    {
        private HashSet<string> _configs = new HashSet<string>
        {
            "ropsten_archive.cfg",
            "ropsten_beam.cfg",
            "ropsten.cfg",
            "rinkeby_archive.cfg",
            "rinkeby_beam.cfg",
            "rinkeby.cfg",
            "goerli_archive.cfg",
            "goerli_beam.cfg",
            "goerli.cfg",
            "mainnet_archive.cfg",
            "mainnet_beam.cfg",
            "mainnet.cfg",
            "sokol.cfg",
            "sokol_archive.cfg",
            "sokol_validator.cfg",
            "poacore.cfg",
            "poacore_archive.cfg",
            "poacore_beam.cfg",
            "poacore_validator.cfg",
            "xdai.cfg",
            "xdai_archive.cfg",
            "xdai_validator.cfg",
            "spaceneth.cfg",
            "volta.cfg",
            "volta_archive.cfg",
        };

        [SetUp]
        public void Setup()
        {
        }


        [TestCase("sokol_validator.cfg", true, true)]
        [TestCase("poacore_validator.cfg", true, true)]
        [TestCase("xdai_validator.cfg", true, true)]
        [TestCase("spaceneth.cfg", false, false)]
        [TestCase("archive", false, false, false)]
        [TestCase("beam", true, true, true)]
        [TestCase("fast", true, true)]
        public void Sync_defaults_are_correct(string configWildcard, bool fastSyncEnabled, bool fastBlocksEnabled, bool beamSyncEnabled = false)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                ISyncConfig config = configProvider.GetConfig<ISyncConfig>();
                config.FastSync.Should().Be(fastSyncEnabled, configFile);
                config.FastBlocks.Should().Be(fastBlocksEnabled, configFile);
                config.BeamSync.Should().Be(beamSyncEnabled, configFile);
            }
        }

        [TestCase("archive", true)]
        [TestCase("fast", true)]
        [TestCase("beam", true)]
        [TestCase("spaceneth.cfg", false)]
        [TestCase("ndm_consumer_goerli.cfg", true)]
        [TestCase("ndm_consumer_local.cfg", true)]
        [TestCase("ndm_consumer_mainnet_proxy.cfg", false)]
        [TestCase("ndm_consumer_ropsten.cfg", true)]
        [TestCase("ndm_consumer_ropsten_proxy.cfg", false)]
        public void Sync_is_disabled_when_needed(string configWildcard, bool isSyncEnabled)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                ISyncConfig config = configProvider.GetConfig<ISyncConfig>();
                Assert.AreEqual(isSyncEnabled, config.SynchronizationEnabled);
            }
        }

        [TestCase("ropsten", "ws://ropsten-stats.parity.io/api")]
        [TestCase("rinkeby", "ws://localhost:3000/api")]
        [TestCase("goerli", "wss://stats.goerli.net/api")]
        [TestCase("mainnet", "wss://ethstats.net/api")]
        [TestCase("sokol", "ws://localhost:3000/api")]
        [TestCase("poacore", "ws://localhost:3000/api")]
        [TestCase("xdai", "ws://localhost:3000/api")]
        [TestCase("spaceneth.cfg", "ws://localhost:3000/api")]
        [TestCase("volta", "ws://localhost:3000/api")]
        public void Ethstats_values_are_correct(string configWildcard, string host)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IEthStatsConfig config = configProvider.GetConfig<IEthStatsConfig>();
                Assert.AreEqual(host, config.Server);
            }
        }

        [TestCase("aura", false)]
        [TestCase("ethhash", true)]
        [TestCase("clique", true)]
        public void Geth_limits_configs_are_correct(string configWildcard, bool useGethLimitsInFastSync)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                ISyncConfig config = configProvider.GetConfig<ISyncConfig>();
                Assert.AreEqual(useGethLimitsInFastSync, config.UseGethLimitsInFastBlocks);
            }
        }

        [TestCase("ropsten", "0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d")]
        [TestCase("rinkeby", "0x6341fd3daf94b748c72ced5a5b26028f2474f5f00d824504e4fa37a75767e177")]
        [TestCase("goerli", "0xbf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1a")]
        [TestCase("mainnet", "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3")]
        [TestCase("sokol", "0x5b28c1bfd3a15230c9a46b399cd0f9a6920d432e85381cc6a140b06e8410112f")]
        [TestCase("poacore", "0x39f02c003dde5b073b3f6e1700fc0b84b4877f6839bb23edadd3d2d82a488634")]
        [TestCase("xdai", "0x4f1dd23188aab3a76b463e4af801b52b1248ef073c648cbdc4c9333d3da79756")]
        [TestCase("volta", "0xebd8b413ca7b7f84a8dd20d17519ce2b01954c74d94a0a739a3e416abe0e43e5")]
        public void Genesis_hash_is_correct(string configWildcard, string genesisHash)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IInitConfig config = configProvider.GetConfig<IInitConfig>();
                Assert.AreEqual(config.GenesisHash, genesisHash);
            }
        }

        [TestCase("spaceneth.cfg", true)]
        [TestCase("validators", true)]
        [TestCase("^validators ^spaceneth.cfg", false)]
        public void Mining_defaults_are_correct(string configWildcard, bool defaultValue = false)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IInitConfig config = configProvider.GetConfig<IInitConfig>();
                config.IsMining.Should().Be(defaultValue, configFile);
            }
        }

        [TestCase("*")]
        public void Required_config_files_exist(string configWildcard)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                var configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
                Assert.True(File.Exists(configPath));
            }
        }

        [TestCase("*")]
        public void Eth_stats_disabled_by_default(string configWildcard)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IEthStatsConfig config = configProvider.GetConfig<IEthStatsConfig>();
                Assert.AreEqual(config.Enabled, false);
            }
        }

        [TestCase("^ndm")]
        public void Grpc_disabled_by_default(string configWildcard)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IGrpcConfig config = configProvider.GetConfig<IGrpcConfig>();
                Assert.AreEqual(false, config.Enabled);
                Assert.AreEqual(false, config.ProducerEnabled);
            }
        }

        [TestCase("ndm")]
        public void Grpc_enabled_for_ndm(string configWildcard)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IGrpcConfig config = configProvider.GetConfig<IGrpcConfig>();
                Assert.AreEqual(true, config.Enabled);
                Assert.AreEqual(false, config.ProducerEnabled);
            }
        }

        [TestCase("ndm_consumer_local.cfg")]
        public void IsMining_enabled_for_ndm_consumer_local(string configWildcard)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IInitConfig config = configProvider.GetConfig<IInitConfig>();
                Assert.AreEqual(true, config.IsMining);
            }
        }

        [TestCase("ndm", true)]
        [TestCase("^ndm", false)]
        public void Ndm_enabled_only_for_ndm_configs(string configWildcard, bool ndmEnabled)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                INdmConfig config = configProvider.GetConfig<INdmConfig>();
                Assert.AreEqual(config.Enabled, ndmEnabled);
            }
        }

        [TestCase("*")]
        public void Metrics_disabled_by_default(string configWildcard)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IMetricsConfig config = configProvider.GetConfig<IMetricsConfig>();
                Assert.AreEqual(config.Enabled, false);
            }
        }

        [TestCase("^mainnet", 50)]
        [TestCase("mainnet", 100)]
        public void Network_defaults_are_correct(string configWildcard, int activePeers = 50)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
                Assert.AreEqual(30303, networkConfig.DiscoveryPort, nameof(networkConfig.DiscoveryPort));
                Assert.AreEqual(30303, networkConfig.P2PPort, nameof(networkConfig.P2PPort));
                Assert.Null(networkConfig.ExternalIp, nameof(networkConfig.ExternalIp));
                Assert.Null(networkConfig.LocalIp, nameof(networkConfig.LocalIp));
                networkConfig.ActivePeersMaxCount.Should().Be(activePeers, configFile);
            }
        }

        [TestCase("^spaceneth.cfg", false)]
        public void Json_defaults_are_correct(string configWildcard, bool jsonEnabled)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IJsonRpcConfig jsonRpcConfig = configProvider.GetConfig<IJsonRpcConfig>();
                Assert.AreEqual(8545, jsonRpcConfig.Port, nameof(jsonRpcConfig.Port));
                Assert.AreEqual("127.0.0.1", jsonRpcConfig.Host, nameof(jsonRpcConfig.Host));
                jsonRpcConfig.Enabled.Should().Be(jsonEnabled, configFile);
            }
        }

        [TestCase("*")]
        public void Kafka_disabled_by_default(string configWildcard)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IKafkaConfig kafkaConfig = configProvider.GetConfig<IKafkaConfig>();
                Assert.AreEqual(false, kafkaConfig.Enabled, nameof(kafkaConfig.Enabled));
            }
        }

        [TestCase("^mainnet ^validators ^beam ^archive", true, true)]
        [TestCase("mainnet ^beam", false, false)]
        [TestCase("beam", false, false, false)]
        [TestCase("validators", true, false)]
        public void Fast_sync_settings_as_expected(string configWildcard, bool downloadBodies, bool downloadsReceipts, bool downloadHeaders = true)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
                syncConfig.DownloadBodiesInFastSync.Should().Be(downloadBodies, configFile);
                syncConfig.DownloadReceiptsInFastSync.Should().Be(downloadsReceipts, configFile);
                syncConfig.DownloadHeadersInFastSync.Should().Be(downloadHeaders, configFile);
            }
        }

        [TestCase("^aura", false)]
        [TestCase("aura ^archive", true)]
        public void Stays_on_full_sync(string configWildcard, bool stickToFullSyncAfterFastSync)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
                if (stickToFullSyncAfterFastSync)
                {
                    syncConfig.FastSyncCatchUpHeightDelta.Should().BeGreaterOrEqualTo(1000000000, configFile);
                }
                else
                {
                    syncConfig.FastSyncCatchUpHeightDelta.Should().Be(1024, configFile);
                }
            }
        }

        [TestCase("^validators", true)]
        [TestCase("validators", false)]
        public void Stores_receipts(string configWildcard, bool storeReceipts)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
                initConfig.StoreReceipts.Should().Be(storeReceipts, configFile);
            }
        }

        [TestCase("ropsten", false)]
        [TestCase("rinkeby", false)]
        [TestCase("goerli", false)]
        [TestCase("mainnet_archive.cfg", true)]
        [TestCase("mainnet.cfg", true)]
        [TestCase("sokol", false)]
        [TestCase("poacore", true)]
        [TestCase("xdai", true)]
        [TestCase("volta", false)]
        public void Basic_configs_are_as_expected(string configWildcard, bool isProduction = false)
        {
            foreach (string configFile in Resolve(configWildcard))
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

                Assert.AreEqual(configFile.Replace("cfg", "logs.txt"), initConfig.LogFileName, nameof(initConfig.LogFileName));
            }
        }


        [TestCase("ropsten")]
        [TestCase("rinkeby")]
        [TestCase("goerli", new[] {16, 16, 16, 16})]
        [TestCase("mainnet")]
        [TestCase("sokol.cfg", new[] {16, 16, 16, 16})]
        [TestCase("sokol_archive.cfg", new[] {16, 16, 16, 16})]
        [TestCase("sokol_validator.cfg", null, false)]
        [TestCase("poacore.cfg", new[] {16, 16, 16, 16})]
        [TestCase("poacore_archive.cfg", new[] {16, 16, 16, 16})]
        [TestCase("poacore_validator.cfg", null, false)]
        [TestCase("xdai.cfg", new[] {16, 16, 16})]
        [TestCase("xdai_archive.cfg", new[] {16, 16, 16})]
        [TestCase("xdai_validator.cfg", null, false)]
        [TestCase("volta")]
        public void Bloom_configs_are_as_expected(string configWildcard, int[] levels = null, bool index = true)
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                IBloomConfig bloomConfig = configProvider.GetConfig<IBloomConfig>();
                bloomConfig.Index.Should().Be(index, configFile);
                bloomConfig.Migration.Should().BeFalse(configFile);
                bloomConfig.MigrationStatistics.Should().BeFalse(configFile);
                bloomConfig.IndexLevelBucketSizes.Should().Equal(levels ?? new BloomConfig().IndexLevelBucketSizes, configFile);
            }
        }

        private static ConfigProvider GetConfigProviderFromFile(string configFile)
        {
            ConfigProvider configProvider = new ConfigProvider();
            var configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
            configProvider.AddSource(new JsonConfigSource(configPath));
            return configProvider;
        }

        private IEnumerable<string> BeamConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("_beam"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> FastSyncConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (!config.Contains("_") && !config.Contains("spaceneth"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> ArchiveConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("_archive"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> RopstenConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("ropsten"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> PoaCoreConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("poacore"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> SokolConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("sokol"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> VoltaConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("volta"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> XDaiConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("xdai"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> GoerliConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("goerli"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> RinkebyConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("rinkeby"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> MainnetConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("mainnet"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> ValidatorConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("validator"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> NdmConfigs
        {
            get
            {
                foreach (string config in _configs)
                {
                    if (config.Contains("ndm"))
                    {
                        yield return config;
                    }
                }
            }
        }

        private IEnumerable<string> Resolve(string configWildcard)
        {
            string[] configWildcards = configWildcard.Split(" ");
            List<IEnumerable<string>> toIntersect = new List<IEnumerable<string>>();
            foreach (string singleWildcard in configWildcards)
            {
                string singleWildcardBase = singleWildcard.Replace("^", "");
                var result = (singleWildcard.Replace("^", "")) switch
                {
                    "*" => _configs,
                    "archive" => ArchiveConfigs,
                    "fast" => FastSyncConfigs,
                    "beam" => BeamConfigs,
                    "mainnet" => MainnetConfigs,
                    "goerli" => GoerliConfigs,
                    "rinkeby" => RinkebyConfigs,
                    "ropsten" => RopstenConfigs,
                    "sokol" => SokolConfigs,
                    "poacore" => PoaCoreConfigs,
                    "volta" => VoltaConfigs,
                    "xdai" => XDaiConfigs,
                    "validators" => ValidatorConfigs,
                    "ndm" => NdmConfigs,
                    "aura" => PoaCoreConfigs.Union(SokolConfigs).Union(XDaiConfigs).Union(VoltaConfigs),
                    "aura_non_validating" => PoaCoreConfigs.Union(SokolConfigs).Union(XDaiConfigs).Union(VoltaConfigs).Where(c => !c.Contains("validator")),
                    "clique" => RinkebyConfigs.Union(GoerliConfigs),
                    "ethhash" => MainnetConfigs.Union(RopstenConfigs),
                    _ => Enumerable.Repeat(singleWildcardBase, 1)
                };

                if (singleWildcard.StartsWith("^"))
                {
                    result = _configs.Except(result);
                }

                toIntersect.Add(result);
            }

            var intersection = toIntersect.First();
            foreach (IEnumerable<string> next in toIntersect.Skip(1))
            {
                intersection = intersection.Intersect(next);
            }

            return intersection;
        }
    }
}