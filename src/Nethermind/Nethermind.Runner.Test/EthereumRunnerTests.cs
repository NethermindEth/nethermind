// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Db.Rocks.Config;
using Nethermind.EthStats;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum;
using Nethermind.Db.Blooms;
using Nethermind.Logging.NLog;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.TxPool;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using LogLevel = NLog.LogLevel;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class EthereumRunnerTests
    {
        private static readonly Lazy<ICollection> _cachedProviders = new(InitOnce);

        public static ICollection InitOnce()
        {
            // by pre-caching configs providers we make the tests do lot less work
            ConcurrentQueue<(string, ConfigProvider)> result = new();
            Parallel.ForEach(Directory.GetFiles("configs"), configFile =>
            {
                var configProvider = new ConfigProvider();
                configProvider.AddSource(new JsonConfigSource(configFile));
                configProvider.Initialize();
                result.Enqueue((configFile, configProvider));
            });

            return result;
        }

        public static IEnumerable ChainSpecRunnerTests
        {
            get
            {
                int index = 0;
                foreach (var cachedProvider in _cachedProviders.Value)
                {

                    yield return new TestCaseData(cachedProvider, index);
                    index++;
                }
            }
        }

        [TestCaseSource(nameof(ChainSpecRunnerTests))]
        [Timeout(300000)] // just to make sure we are not on infinite loop on steps because of incorrect dependencies
        public async Task Smoke((string file, ConfigProvider configProvider) testCase, int testIndex)
        {
            if (testCase.configProvider is null)
            {
                // some weird thing, not worth investigating
                return;
            }

            await SmokeTest(testCase.configProvider, testIndex, 30330);
        }

        [TestCaseSource(nameof(ChainSpecRunnerTests))]
        [Timeout(30000)] // just to make sure we are not on infinite loop on steps because of incorrect dependencies
        public async Task Smoke_cancel((string file, ConfigProvider configProvider) testCase, int testIndex)
        {
            if (testCase.configProvider is null)
            {
                // some weird thing, not worth investigating
                return;
            }

            await SmokeTest(testCase.configProvider, testIndex, 30430, true);
        }

        private static async Task SmokeTest(ConfigProvider configProvider, int testIndex, int basePort, bool cancel = false)
        {
            Type type1 = typeof(ITxPoolConfig);
            Type type2 = typeof(INetworkConfig);
            Type type3 = typeof(IKeyStoreConfig);
            Type type4 = typeof(IDbConfig);
            Type type7 = typeof(IEthStatsConfig);
            Type type8 = typeof(ISyncConfig);
            Type type9 = typeof(IBloomConfig);

            Console.WriteLine(type1.Name);
            Console.WriteLine(type2.Name);
            Console.WriteLine(type3.Name);
            Console.WriteLine(type4.Name);
            Console.WriteLine(type7.Name);
            Console.WriteLine(type8.Name);
            Console.WriteLine(type9.Name);

            var tempPath = TempPath.GetTempDirectory();
            Directory.CreateDirectory(tempPath.Path);

            Exception? exception = null;
            try
            {
                IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
                initConfig.BaseDbPath = tempPath.Path;

                INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
                int port = basePort + testIndex;
                networkConfig.P2PPort = port;
                networkConfig.DiscoveryPort = port;

                INethermindApi nethermindApi = new ApiBuilder(configProvider, LimboLogs.Instance).Create();
                nethermindApi.RpcModuleProvider = new RpcModuleProvider(new FileSystem(), new JsonRpcConfig(), LimboLogs.Instance);
                EthereumRunner runner = new(nethermindApi);

                using CancellationTokenSource cts = new();

                try
                {
                    Task task = runner.Start(cts.Token);
                    if (cancel)
                    {
                        cts.Cancel();
                    }

                    await task;
                }
                catch (Exception e)
                {
                    exception = e;
                }
                finally
                {
                    try
                    {
                        await runner.StopAsync();
                    }
                    catch (Exception e)
                    {
                        if (exception is not null)
                        {
                            await TestContext.Error.WriteLineAsync(e.ToString());
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    tempPath.Dispose();
                }
                catch
                {
                    if (exception is not null)
                    {
                        // just swallow this exception as otherwise this is recognized as a pattern byt GitHub
                        // await TestContext.Error.WriteLineAsync(e.ToString());
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
    }
}
