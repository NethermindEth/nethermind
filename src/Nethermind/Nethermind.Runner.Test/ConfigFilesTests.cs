// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Analytics;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Config.Test;
using Nethermind.Db;
using Nethermind.EthStats;
using Nethermind.JsonRpc;
using Nethermind.Monitoring.Config;
using Nethermind.Network.Config;
using Nethermind.Db.Blooms;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ConfigFilesTests : ConfigFileTestsBase
    {
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
        [TestCase("spaceneth", false, false)]
        [TestCase("archive", false, false)]
        [TestCase("fast", true, true)]
        public void Sync_defaults_are_correct(string configWildcard, bool fastSyncEnabled, bool fastBlocksEnabled)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.FastSync, fastSyncEnabled);
            Test<ISyncConfig, bool>(configWildcard, c => c.FastBlocks, fastBlocksEnabled);
        }

        [TestCase("archive")]
        public void Archive_configs_have_pruning_turned_off(string configWildcard)
        {
            Test<IPruningConfig, PruningMode>(configWildcard, c => c.Mode, PruningMode.None);
        }

        [TestCase("archive", true)]
        [TestCase("fast", true)]
        [TestCase("spaceneth", false)]
        public void Sync_is_disabled_when_needed(string configWildcard, bool isSyncEnabled)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.SynchronizationEnabled, isSyncEnabled);
        }

        [TestCase("archive", true)]
        [TestCase("fast", true)]
        [TestCase("spaceneth", false)]
        public void Networking_is_disabled_when_needed(string configWildcard, bool isEnabled)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.NetworkingEnabled, isEnabled);
        }

        [TestCase("sepolia", "ws://localhost:3000/api")]
        [TestCase("goerli", "wss://stats.goerli.net/api")]
        [TestCase("mainnet", "wss://ethstats.net/api")]
        [TestCase("poacore", "ws://localhost:3000/api")]
        [TestCase("gnosis", "ws://localhost:3000/api")]
        [TestCase("spaceneth", "ws://localhost:3000/api")]
        [TestCase("volta", "ws://localhost:3000/api")]
        public void Ethstats_values_are_correct(string configWildcard, string host)
        {
            Test<IEthStatsConfig, bool>(configWildcard, c => c.Enabled, false);
            Test<IEthStatsConfig, string>(configWildcard, c => c.Server, host);
            Test<IEthStatsConfig, string>(configWildcard, c => c.Secret, "secret");
            Test<IEthStatsConfig, string>(configWildcard, c => c.Contact, "hello@nethermind.io");
        }

        [TestCase("aura ^archive", false)]
        [TestCase("clique", true)]
        public void Geth_limits_configs_are_correct(string configWildcard, bool useGethLimitsInFastSync)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.UseGethLimitsInFastBlocks, useGethLimitsInFastSync);
        }

        [TestCase("goerli", "0xbf7e331f7f7c1dd2e05159666b3bf8bc7a8a3a9eb1d518969eab529dd9b88c1a")]
        [TestCase("mainnet", "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3")]
        [TestCase("poacore", "0x39f02c003dde5b073b3f6e1700fc0b84b4877f6839bb23edadd3d2d82a488634")]
        [TestCase("gnosis", "0x4f1dd23188aab3a76b463e4af801b52b1248ef073c648cbdc4c9333d3da79756")]
        [TestCase("volta", "0xebd8b413ca7b7f84a8dd20d17519ce2b01954c74d94a0a739a3e416abe0e43e5")]
        public void Genesis_hash_is_correct(string configWildcard, string genesisHash)
        {
            Test<IInitConfig, string>(configWildcard, c => c.GenesisHash, genesisHash);
        }

        [TestCase("spaceneth", true)]
        [TestCase("validators", true)]
        [TestCase("^validators ^spaceneth", false)]
        public void Mining_defaults_are_correct(string configWildcard, bool defaultValue = false)
        {
            Test<IInitConfig, bool>(configWildcard, c => c.IsMining, defaultValue);
        }

        [TestCase("*")]
        public void Eth_stats_disabled_by_default(string configWildcard)
        {
            Test<IEthStatsConfig, bool>(configWildcard, c => c.Enabled, false);
        }

        [TestCase("*")]
        public void Analytics_defaults(string configWildcard)
        {
            Test<IAnalyticsConfig, bool>(configWildcard, c => c.PluginsEnabled, false);
            Test<IAnalyticsConfig, bool>(configWildcard, c => c.StreamBlocks, false);
            Test<IAnalyticsConfig, bool>(configWildcard, c => c.StreamTransactions, false);
            Test<IAnalyticsConfig, bool>(configWildcard, c => c.LogPublishedData, false);
        }

        [TestCase("fast")]
        public void Caches_in_fast_blocks(string configWildcard)
        {
            Test<IDbConfig, bool>(configWildcard, c => c.HeadersDbCacheIndexAndFilterBlocks, false);
            Test<IDbConfig, bool>(configWildcard, c => c.ReceiptsDbCacheIndexAndFilterBlocks, false);
            Test<IDbConfig, bool>(configWildcard, c => c.BlocksDbCacheIndexAndFilterBlocks, false);
            Test<IDbConfig, bool>(configWildcard, c => c.BlockInfosDbCacheIndexAndFilterBlocks, false);
        }

        [TestCase("^archive", false)]
        [TestCase("archive", false)]
        public void Cache_state_index(string configWildcard, bool expectedValue)
        {
            Test<IDbConfig, bool>(configWildcard, c => c.CacheIndexAndFilterBlocks, expectedValue);
        }

        [TestCase("mainnet archive", 4096000000)]
        [TestCase("mainnet ^archive", 2048000000)]
        [TestCase("volta archive", 768000000)]
        [TestCase("volta ^archive", 768000000)]
        [TestCase("goerli archive", 768000000)]
        [TestCase("goerli ^archive", 768000000)]
        [TestCase("gnosis archive", 1024000000)]
        [TestCase("gnosis ^archive", 768000000)]
        [TestCase("poacore archive", 1024000000)]
        [TestCase("poacore ^archive", 768000000)]
        [TestCase("spaceneth.cfg", 64000000)]
        [TestCase("spaceneth_persistent.cfg", 128000000)]
        public void Memory_hint_values_are_correct(string configWildcard, long expectedValue)
        {
            Test<IInitConfig, long?>(configWildcard, c => c.MemoryHint, expectedValue);
        }

        [TestCase("*")]
        public void Metrics_disabled_by_default(string configWildcard)
        {
            Test<IMetricsConfig, bool>(configWildcard, c => c.Enabled, false);
            Test<IMetricsConfig, string>(configWildcard, c => c.NodeName.ToUpperInvariant(), (cf, p) => cf.Replace("_", " ").Replace(".cfg", "").ToUpperInvariant().Replace("POACORE", "POA CORE"));
            Test<IMetricsConfig, int>(configWildcard, c => c.IntervalSeconds, 5);
            Test<IMetricsConfig, string>(configWildcard, c => c.PushGatewayUrl, "");
        }

        [TestCase("^mainnet ^spaceneth ^volta", 50)]
        [TestCase("spaceneth", 4)]
        [TestCase("volta", 25)]
        [TestCase("mainnet", 100)]
        public void Network_defaults_are_correct(string configWildcard, int activePeers = 50)
        {
            Test<INetworkConfig, int>(configWildcard, c => c.DiscoveryPort, 30303);
            Test<INetworkConfig, int>(configWildcard, c => c.P2PPort, 30303);
            Test<INetworkConfig, string>(configWildcard, c => c.ExternalIp, (string)null);
            Test<INetworkConfig, string>(configWildcard, c => c.LocalIp, (string)null);
            Test<INetworkConfig, int>(configWildcard, c => c.MaxActivePeers, activePeers);
        }

        [TestCase("*")]
        public void Network_diag_tracer_disabled_by_default(string configWildcard)
        {
            Test<INetworkConfig, bool>(configWildcard, c => c.DiagTracerEnabled, false);
        }

        [TestCase("mainnet", 2048)]
        [TestCase("holesky", 1024)]
        [TestCase("sepolia", 1024)]
        [TestCase("gnosis", 2048)]
        [TestCase("poacore", 2048)]
        [TestCase("energy", 2048)]
        [TestCase("chiado", 1024)]
        [TestCase("^mainnet ^spaceneth ^volta ^energy ^poacore ^gnosis ^chiado", 1024)]
        [TestCase("spaceneth", 128)]
        public void Tx_pool_defaults_are_correct(string configWildcard, int poolSize)
        {
            Test<ITxPoolConfig, int>(configWildcard, c => c.Size, poolSize);
        }

        [TestCase("spaceneth", true)]
        [TestCase("goerli", true)]
        [TestCase("gnosis", true)]
        [TestCase("mainnet", true)]
        [TestCase("sepolia", true)]
        [TestCase("holesky", true)]
        [TestCase("chiado", true)]
        [TestCase("^spaceneth ^goerli ^mainnet ^gnosis ^sepolia ^holesky ^chiado", false)]
        public void Json_defaults_are_correct(string configWildcard, bool jsonEnabled)
        {
            Test<IJsonRpcConfig, bool>(configWildcard, c => c.Enabled, jsonEnabled);
            Test<IJsonRpcConfig, int>(configWildcard, c => c.Port, 8545);
            Test<IJsonRpcConfig, string>(configWildcard, c => c.Host, "127.0.0.1");
        }

        [TestCase("*")]
        public void Tracer_timeout_default_is_correct(string configWildcard)
        {
            Test<IJsonRpcConfig, int>(configWildcard, c => c.Timeout, 20000);
        }

        [TestCase("^mainnet ^validators ^archive", true, true)]
        [TestCase("mainnet ^fast", false, false)]
        [TestCase("mainnet fast", true, true)]
        [TestCase("validators", true, false)]
        public void Fast_sync_settings_as_expected(string configWildcard, bool downloadBodies, bool downloadsReceipts, bool downloadHeaders = true)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.DownloadBodiesInFastSync, downloadBodies);
            Test<ISyncConfig, bool>(configWildcard, c => c.DownloadReceiptsInFastSync, downloadsReceipts);
            Test<ISyncConfig, bool>(configWildcard, c => c.DownloadHeadersInFastSync, downloadHeaders);
        }

        [TestCase("archive", false)]
        [TestCase("mainnet.cfg", true)]
        [TestCase("goerli.cfg", true)]
        [TestCase("sepolia.cfg", true)]
        [TestCase("gnosis.cfg", false)]
        public void Snap_sync_settings_as_expected(string configWildcard, bool enabled)
        {
            Test<ISyncConfig, bool>(configWildcard, c => c.SnapSync, enabled);
        }

        [TestCase("^aura ^sepolia ^holesky ^goerli ^mainnet", false)]
        [TestCase("aura ^archive", true)]
        [TestCase("^archive ^spaceneth", true)]
        [TestCase("sepolia ^archive", true)]
        [TestCase("holesky ^archive", true)]
        [TestCase("goerli ^archive", true)]
        [TestCase("mainnet ^archive", true)]
        public void Stays_on_full_sync(string configWildcard, bool stickToFullSyncAfterFastSync)
        {
            Test<ISyncConfig, long?>(configWildcard, c => c.FastSyncCatchUpHeightDelta, stickToFullSyncAfterFastSync ? 10_000_000_000 : 8192);
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

        [TestCase("^mainnet", 0)]
        [TestCase("mainnet fast", 0)]
        public void Barriers_defaults_are_correct(string configWildcard, long barrier)
        {
            Test<ISyncConfig, long>(configWildcard, c => c.AncientBodiesBarrier, barrier);
            Test<ISyncConfig, long>(configWildcard, c => c.AncientReceiptsBarrier, barrier);
        }

        [TestCase("^spaceneth", "nethermind_db")]
        [TestCase("spaceneth", "spaceneth_db")]
        public void Base_db_path_is_set(string configWildcard, string startWith)
        {
            Test<IInitConfig, string>(configWildcard, c => c.BaseDbPath, (cf, p) => p.Should().StartWith(startWith));
        }

        [TestCase("^sepolia", "Data/static-nodes.json")]
        [TestCase("sepolia", "Data/static-nodes-sepolia.json")]
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
            Test<ISyncConfig, int>(configWildcard, c => (int)(c.PivotNumberParsed % 30000L), (s, p) => p.Should().Be(0));
        }

        [TestCase("goerli", false)]
        [TestCase("mainnet_archive.cfg", true)]
        [TestCase("mainnet.cfg", true)]
        [TestCase("poacore", true)]
        [TestCase("gnosis", true)]
        [TestCase("volta", false)]
        public void Basic_configs_are_as_expected(string configWildcard, bool isProduction = false)
        {
            Test<IInitConfig, bool>(configWildcard, c => c.DiscoveryEnabled, true);
            Test<IInitConfig, bool>(configWildcard, c => c.ProcessingEnabled, true);
            Test<IInitConfig, bool>(configWildcard, c => c.WebSocketsEnabled, true);
            Test<IInitConfig, bool>(configWildcard, c => c.PeerManagerEnabled, true);
            Test<IInitConfig, bool>(configWildcard, c => c.KeepDevWalletInMemory, false);

            if (isProduction)
            {
                Test<IInitConfig, bool>(configWildcard, c => c.EnableUnsecuredDevWallet, false);
            }

            Test<IInitConfig, string>(configWildcard, c => c.LogFileName, (cf, p) => p.Should().Be(cf.Replace("cfg", "logs.txt"), cf));
        }

        [TestCase("*")]
        public void Simulating_block_production_on_every_slot_is_always_disabled(string configWildcard)
        {
            Test<IMergeConfig, bool>(configWildcard, c => c.SimulateBlockProduction, false);
        }

        [TestCase("goerli", BlobsSupportMode.StorageWithReorgs)]
        [TestCase("sepolia", BlobsSupportMode.StorageWithReorgs)]
        [TestCase("holesky", BlobsSupportMode.StorageWithReorgs)]
        [TestCase("chiado", BlobsSupportMode.StorageWithReorgs)]
        [TestCase("mainnet", BlobsSupportMode.Disabled)]
        [TestCase("gnosis", BlobsSupportMode.Disabled)]
        public void Blob_txs_support_is_correct(string configWildcard, BlobsSupportMode blobsSupportMode)
        {
            Test<ITxPoolConfig, BlobsSupportMode>(configWildcard, c => c.BlobsSupport, blobsSupportMode);
        }


        [TestCase("goerli", new[] { 16, 16, 16, 16 })]
        [TestCase("mainnet")]
        [TestCase("poacore.cfg", new[] { 16, 16, 16, 16 })]
        [TestCase("poacore_archive.cfg", new[] { 16, 16, 16, 16 })]
        [TestCase("poacore_validator.cfg", null, false)]
        [TestCase("gnosis.cfg", new[] { 16, 16, 16 })]
        [TestCase("gnosis_archive.cfg", new[] { 16, 16, 16 })]
        [TestCase("volta")]
        public void Bloom_configs_are_as_expected(string configWildcard, int[] levels = null, bool index = true)
        {
            Test<IBloomConfig, bool>(configWildcard, c => c.Index, index);
            Test<IBloomConfig, bool>(configWildcard, c => c.Migration, false);
            Test<IBloomConfig, bool>(configWildcard, c => c.MigrationStatistics, false);
            Test<IBloomConfig, int[]>(configWildcard, c => c.IndexLevelBucketSizes, (cf, p) => p.Should().BeEquivalentTo(levels ?? new BloomConfig().IndexLevelBucketSizes));
        }

        [TestCase("*")]
        public void BufferResponses_rpc_is_off(string configWildcard)
        {
            Test<IJsonRpcConfig, bool>(configWildcard, c => c.BufferResponses, false);
        }

        [TestCase("*")]
        public void Arena_order_is_default(string configWildcard)
        {
            Test<INetworkConfig, int>(configWildcard, c => c.NettyArenaOrder, -1);
        }

        [TestCase("chiado", 17_000_000L, 5UL)]
        [TestCase("gnosis", 17_000_000L, 5UL)]
        [TestCase("goerli", 30_000_000L)]
        [TestCase("mainnet", 30_000_000L)]
        [TestCase("sepolia", 30_000_000L)]
        [TestCase("holesky", 30_000_000L)]
        [TestCase("^chiado ^gnosis ^goerli ^mainnet ^sepolia ^holesky")]
        public void Blocks_defaults_are_correct(string configWildcard, long? targetBlockGasLimit = null, ulong secondsPerSlot = 12)
        {
            Test<IBlocksConfig, long?>(configWildcard, c => c.TargetBlockGasLimit, targetBlockGasLimit);
            Test<IBlocksConfig, ulong>(configWildcard, c => c.SecondsPerSlot, secondsPerSlot);
        }

        [Test]
        public void No_additional_commas_in_config_files()
        {
            char pathSeparator = Path.AltDirectorySeparatorChar;
            string configDirectory = $"{AppDomain.CurrentDomain.BaseDirectory}{pathSeparator}configs";

            IEnumerable<string> filesPaths = Directory.EnumerateFiles(configDirectory);

            foreach (string filePath in filesPaths)
            {
                string content = File.ReadAllText(filePath)
                    .Replace("\n", string.Empty)
                    .Replace(" ", string.Empty);

                IEnumerable<int> commaIndexes = AllIndexesOf(content, ",");

                foreach (int commaIndex in commaIndexes)
                {
                    var nextChar = content.ElementAt(commaIndex + 1);
                    Assert.That(nextChar, Is.Not.EqualTo('}'), $"Additional comma found in {filePath}");
                }
            }
        }

        [TestCase("*")]
        public void Memory_hint_is_enough(string configWildcard)
        {
            foreach (TestConfigProvider configProvider in GetConfigProviders(configWildcard))
            {
                MemoryHintMan memoryHintMan = new(LimboLogs.Instance);
                memoryHintMan.SetMemoryAllowances(
                    configProvider.GetConfig<IDbConfig>(),
                    configProvider.GetConfig<IInitConfig>(),
                    configProvider.GetConfig<INetworkConfig>(),
                    configProvider.GetConfig<ISyncConfig>(),
                    configProvider.GetConfig<ITxPoolConfig>(),
                    (uint)Environment.ProcessorCount);
            }
        }

        protected override IEnumerable<string> Configs { get; } = new HashSet<string>
        {
            "goerli_archive.cfg",
            "goerli.cfg",
            "holesky.cfg",
            "holesky_archive.cfg",
            "mainnet_archive.cfg",
            "mainnet.cfg",
            "poacore.cfg",
            "poacore_archive.cfg",
            "gnosis.cfg",
            "gnosis_archive.cfg",
            "spaceneth.cfg",
            "spaceneth_persistent.cfg",
            "volta.cfg",
            "volta_archive.cfg",
            "energyweb.cfg",
            "energyweb_archive.cfg",
            "sepolia.cfg",
            "sepolia_archive.cfg",
            "chiado.cfg",
            "chiado_archive.cfg",
        };

        public IEnumerable<int> AllIndexesOf(string str, string searchString)
        {
            int minIndex = str.IndexOf(searchString);
            while (minIndex != -1)
            {
                yield return minIndex;
                minIndex = str.IndexOf(searchString, minIndex + searchString.Length);
            }
        }
    }
}
