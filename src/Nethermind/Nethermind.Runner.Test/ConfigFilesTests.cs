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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ConfigFilesTests
    {
        [SetUp]
        public void Setup()
        {
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

        [TestCase("validators", true, true)]
        [TestCase("poacore_validator.cfg", true, true)]
        [TestCase("xdai_validator.cfg", true, true)]
        [TestCase("spaceneth.cfg", false, false)]
        [TestCase("archive", false, false, false)]
        [TestCase("beam", true, true, true)]
        [TestCase("fast", true, true)]
        public void Sync_defaults_are_correct(string configWildcard, bool fastSyncEnabled, bool fastBlocksEnabled, bool beamSyncEnabled = false)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.FastSync, fastSyncEnabled);
            Test<ISyncConfig, bool>(configWildcard, c => c.FastBlocks, fastBlocksEnabled);
            Test<ISyncConfig, bool>(configWildcard, c => c.BeamSync, beamSyncEnabled);
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
            Test<ISyncConfig, bool>(configWildcard, c => c.SynchronizationEnabled, isSyncEnabled);
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
            Test<IEthStatsConfig, bool>(configWildcard, c => c.Enabled, false);
            Test<IEthStatsConfig, string>(configWildcard, c => c.Server, host);
            Test<IEthStatsConfig, string>(configWildcard, c => c.Secret, "secret");
            Test<IEthStatsConfig, string>(configWildcard, c => c.Contact, "hello@nethermind.io");
        }

        [TestCase("aura", false)]
        [TestCase("ethhash", true)]
        [TestCase("clique", true)]
        public void Geth_limits_configs_are_correct(string configWildcard, bool useGethLimitsInFastSync)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.UseGethLimitsInFastBlocks, useGethLimitsInFastSync);
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
            Test<IInitConfig, string>(configWildcard, c => c.GenesisHash, genesisHash);
        }

        [TestCase("spaceneth.cfg", true)]
        [TestCase("validators", true)]
        [TestCase("^validators ^spaceneth.cfg", false)]
        public void Mining_defaults_are_correct(string configWildcard, bool defaultValue = false)
        {
            Test<IInitConfig, bool>(configWildcard, c => c.IsMining, defaultValue);
        }

        [TestCase("*")]
        public void Eth_stats_disabled_by_default(string configWildcard)
        {
            Test<IEthStatsConfig, bool>(configWildcard, c => c.Enabled, false);
        }

        [TestCase("^ndm", false)]
        [TestCase("ndm", true)]
        public void Grpc_defaults(string configWildcard, bool expectedDefault)
        {
            Test<IGrpcConfig, bool>(configWildcard, c => c.Enabled, expectedDefault);
            Test<IGrpcConfig, bool>(configWildcard, c => c.ProducerEnabled, expectedDefault);
        }

        [TestCase("ndm_consumer_local.cfg")]
        public void IsMining_enabled_for_ndm_consumer_local(string configWildcard)
        {
            Test<IInitConfig, bool>(configWildcard, c => c.IsMining, true);
        }

        [TestCase("ndm", true)]
        [TestCase("^ndm", false)]
        public void Ndm_enabled_only_for_ndm_configs(string configWildcard, bool ndmEnabled)
        {
            Test<INdmConfig, bool>(configWildcard, c => c.Enabled, ndmEnabled);
        }

        [TestCase("*")]
        public void Metrics_disabled_by_default(string configWildcard)
        {
            Test<IMetricsConfig, bool>(configWildcard, c => c.Enabled, false);
            Test<IMetricsConfig, string>(configWildcard, c => c.NodeName.ToUpperInvariant(), (cf, p) => cf.Replace("_", " ").Replace(".cfg", "").ToUpperInvariant().Replace("POACORE", "POA CORE"));
            Test<IMetricsConfig, int>(configWildcard, c => c.IntervalSeconds, 5);
            Test<IMetricsConfig, string>(configWildcard, c => c.PushGatewayUrl, "http://localhost:9091/metrics");
        }

        [TestCase("^mainnet", 50)]
        [TestCase("mainnet", 100)]
        public void Network_defaults_are_correct(string configWildcard, int activePeers = 50)
        {
            Test<INetworkConfig, int>(configWildcard, c => c.DiscoveryPort, 30303);
            Test<INetworkConfig, int>(configWildcard, c => c.P2PPort, 30303);
            Test<INetworkConfig, string>(configWildcard, c => c.ExternalIp, (string) null);
            Test<INetworkConfig, string>(configWildcard, c => c.LocalIp, (string) null);
            Test<INetworkConfig, int>(configWildcard, c => c.ActivePeersMaxCount, activePeers);
        }

        [TestCase("*", 2048)]
        public void Tx_pool_defaults_are_correct(string configWildcard, int poolSize)
        {
            Test<ITxPoolConfig, int>(configWildcard, c => c.Size, poolSize);
        }

        [TestCase("^spaceneth.cfg", false)]
        public void Json_defaults_are_correct(string configWildcard, bool jsonEnabled)
        {
            Test<IJsonRpcConfig, bool>(configWildcard, c => c.Enabled, jsonEnabled);
            Test<IJsonRpcConfig, int>(configWildcard, c => c.Port, 8545);
            Test<IJsonRpcConfig, string>(configWildcard, c => c.Host, "127.0.0.1");
        }

        [TestCase("*")]
        public void Kafka_disabled_by_default(string configWildcard)
        {
            Test<IKafkaConfig, bool>(configWildcard, c => c.Enabled, false);
        }

        [TestCase("^mainnet ^validators ^beam ^archive", true, true)]
        [TestCase("mainnet ^beam", false, false)]
        [TestCase("beam", false, false, false)]
        [TestCase("validators", true, false)]
        public void Fast_sync_settings_as_expected(string configWildcard, bool downloadBodies, bool downloadsReceipts, bool downloadHeaders = true)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.DownloadBodiesInFastSync, downloadBodies);
            Test<ISyncConfig, bool>(configWildcard, c => c.DownloadReceiptsInFastSync, downloadsReceipts);
            Test<ISyncConfig, bool>(configWildcard, c => c.DownloadHeadersInFastSync, downloadHeaders);
        }

        [TestCase("^aura", false)]
        [TestCase("aura ^archive", true)]
        public void Stays_on_full_sync(string configWildcard, bool stickToFullSyncAfterFastSync)
        {
            Test<ISyncConfig, long?>(configWildcard, c => c.FastSyncCatchUpHeightDelta, stickToFullSyncAfterFastSync ? 10_000_000_000 : 1024);
        }

        [TestCase("^spaceneth.cfg")]
        public void Diagnostics_mode_is_not_enabled_by_default(string configWildcard)
        {
            Test<IInitConfig, DiagnosticMode>(configWildcard, c => c.DiagnosticMode, DiagnosticMode.None);
        }

        [TestCase("*")]
        public void Migrations_are_not_enabled_by_default(string configWildcard)
        {
            Test<IInitConfig, bool>(configWildcard, c => c.ReceiptsMigration, false);
            Test<IBloomConfig, bool>(configWildcard, c => c.Migration, false);
            Test<IBloomConfig, bool>(configWildcard, c => c.MigrationStatistics, false);
        }

        [TestCase("^spaceneth.cfg", "nethermind_db")]
        [TestCase("spaceneth.cfg", "spaceneth_db")]
        public void Base_db_path_is_set(string configWildcard, string startWith)
        {
            Test<IInitConfig, string>(configWildcard, c => c.BaseDbPath, (cf, p) => p.Should().StartWith(startWith));
        }

        [TestCase("*", "Data/static-nodes.json")]
        public void Static_nodes_path_is_default(string configWildcard, string staticNodesPath)
        {
            Test<IInitConfig, string>(configWildcard, c => c.StaticNodesPath, staticNodesPath);
        }

        [TestCase("^validators", true)]
        [TestCase("validators", false)]
        public void Stores_receipts(string configWildcard, bool storeReceipts)
        {
            Test<IInitConfig, bool>(configWildcard, c => c.StoreReceipts, storeReceipts);
        }

        [TestCase("clique")]
        public void Clique_pivots_divide_by_30000_epoch_length(string configWildcard)
        {
            Test<ISyncConfig, int>(configWildcard, c => (int) (c.PivotNumberParsed % 30000L), (s, p) => p.Should().Be(0));
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
            Test<IInitConfig, bool>(configWildcard, c => c.DiscoveryEnabled, true);
            Test<IInitConfig, bool>(configWildcard, c => c.ProcessingEnabled, true);
            Test<IInitConfig, bool>(configWildcard, c => c.WebSocketsEnabled, false);
            Test<IInitConfig, bool>(configWildcard, c => c.PeerManagerEnabled, true);
            Test<IInitConfig, bool>(configWildcard, c => c.KeepDevWalletInMemory, false);

            if (isProduction)
            {
                Test<IInitConfig, bool>(configWildcard, c => c.EnableUnsecuredDevWallet, false);
            }
            
            Test<IInitConfig, string>(configWildcard, c => c.LogFileName, (cf, p) => p.Should().Be(cf.Replace("cfg", "logs.txt"), cf));
            
            Test<IInitConfig, string>(configWildcard, c => c.PluginsDirectory, "plugins");
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
            Test<IBloomConfig, bool>(configWildcard, c => c.Index, index);
            Test<IBloomConfig, bool>(configWildcard, c => c.Migration, false);
            Test<IBloomConfig, bool>(configWildcard, c => c.MigrationStatistics, false);
            Test<IBloomConfig, int[]>(configWildcard, c => c.IndexLevelBucketSizes, (cf, p) => p.Should().BeEquivalentTo(levels ?? new BloomConfig().IndexLevelBucketSizes));
        }

        private static ConfigProvider GetConfigProviderFromFile(string configFile)
        {
            ConfigProvider configProvider = new ConfigProvider();
            var configPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "configs", configFile);
            configProvider.AddSource(new JsonConfigSource(configPath));
            return configProvider;
        }

        private void Test<T, TProperty>(string configWildcard, Func<T, TProperty> getter, TProperty expectedValue) where T : IConfig
        {
            Test(configWildcard, getter, (s, v) => v.Should().Be(expectedValue, s));
        }

        private void Test<T, TProperty>(string configWildcard, Func<T, TProperty> getter, Action<string, TProperty> expectedValue) where T : IConfig
        {
            foreach (string configFile in Resolve(configWildcard))
            {
                ConfigProvider configProvider = GetConfigProviderFromFile(configFile);
                T config = configProvider.GetConfig<T>();
                expectedValue(configFile, getter(config));
            }
        }

        [ConfigFileGroup("*")]
        private IEnumerable<string> Configs { get; } = new HashSet<string>
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

        [ConfigFileGroup("beam")]
        private IEnumerable<string> BeamConfigs
            => Configs.Where(config => config.Contains("_beam"));

        [ConfigFileGroup("fast")]
        private IEnumerable<string> FastSyncConfigs
            => Configs.Where(config => !config.Contains("_") && !config.Contains("spaceneth"));

        [ConfigFileGroup("archive")]
        private IEnumerable<string> ArchiveConfigs
            => Configs.Where(config => config.Contains("_archive"));

        [ConfigFileGroup("ropsten")]
        private IEnumerable<string> RopstenConfigs
            => Configs.Where(config => config.Contains("ropsten"));

        [ConfigFileGroup("poacore")]
        private IEnumerable<string> PoaCoreConfigs
            => Configs.Where(config => config.Contains("poacore"));

        [ConfigFileGroup("sokol")]
        private IEnumerable<string> SokolConfigs
            => Configs.Where(config => config.Contains("sokol"));

        [ConfigFileGroup("volta")]
        private IEnumerable<string> VoltaConfigs
            => Configs.Where(config => config.Contains("volta"));

        [ConfigFileGroup("xdai")]
        private IEnumerable<string> XDaiConfigs
            => Configs.Where(config => config.Contains("xdai"));

        [ConfigFileGroup("goerli")]
        private IEnumerable<string> GoerliConfigs
            => Configs.Where(config => config.Contains("goerli"));

        [ConfigFileGroup("rinkeby")]
        private IEnumerable<string> RinkebyConfigs
            => Configs.Where(config => config.Contains("rinkeby"));

        [ConfigFileGroup("mainnet")]
        private IEnumerable<string> MainnetConfigs
            => Configs.Where(config => config.Contains("mainnet"));

        [ConfigFileGroup("validators")]
        private IEnumerable<string> ValidatorConfigs
            => Configs.Where(config => config.Contains("validator"));

        [ConfigFileGroup("ndm")]
        private IEnumerable<string> NdmConfigs
            => Configs.Where(config => config.Contains("ndm"));

        [ConfigFileGroup("aura")]
        private IEnumerable<string> AuraConfigs
            => PoaCoreConfigs.Union(SokolConfigs).Union(XDaiConfigs).Union(VoltaConfigs);

        [ConfigFileGroup("aura_non_validating")]
        private IEnumerable<string> AuraNonValidatingConfigs
            => AuraConfigs.Where(c => !c.Contains("validator"));

        [ConfigFileGroup("clique")]
        private IEnumerable<string> CliqueConfigs
            => RinkebyConfigs.Union(GoerliConfigs);

        [ConfigFileGroup("ethhash")]
        private IEnumerable<string> EthashConfigs
            => MainnetConfigs.Union(RopstenConfigs);

        private IEnumerable<string> Resolve(string configWildcard)
        {
            Dictionary<string, IEnumerable<string>> groups = BuildConfigGroups();
            string[] configWildcards = configWildcard.Split(" ");

            List<IEnumerable<string>> toIntersect = new List<IEnumerable<string>>();
            foreach (string singleWildcard in configWildcards)
            {
                string singleWildcardBase = singleWildcard.Replace("^", "");
                var result = groups.ContainsKey(singleWildcardBase)
                    ? groups[singleWildcardBase]
                    : Enumerable.Repeat(singleWildcardBase, 1);

                if (singleWildcard.StartsWith("^"))
                {
                    result = Configs.Except(result);
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

        private Dictionary<string, IEnumerable<string>> BuildConfigGroups()
        {
            Dictionary<string, IEnumerable<string>> groups = new Dictionary<string, IEnumerable<string>>();
            foreach (PropertyInfo propertyInfo in GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                ConfigFileGroup groupAttribute = propertyInfo.GetCustomAttribute<ConfigFileGroup>();
                if (groupAttribute != null)
                {
                    groups.Add(groupAttribute.Name, (IEnumerable<string>) propertyInfo.GetValue(this));
                }
            }

            return groups;
        }

        private class ConfigFileGroup : Attribute
        {
            public ConfigFileGroup(string name)
            {
                Name = name;
            }

            public string Name { get; private set; }
        }
    }
}