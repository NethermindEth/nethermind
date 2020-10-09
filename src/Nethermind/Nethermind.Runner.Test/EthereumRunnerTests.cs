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
using System.Collections;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions.Execution;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Db.Rocks.Config;
using Nethermind.EthStats;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.PubSub.Kafka;
using Nethermind.Runner.Ethereum;
using Nethermind.Serialization.Json;
using Nethermind.Stats;
using Nethermind.Db.Blooms;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class EthereumRunnerTests
    {
        public static IEnumerable ChainSpecRunnerTests
        {
            get
            {
                string[] files = Directory.GetFiles("configs");
                for (var index = 0; index < files.Length; index++)
                {
                    var config = files[index];
                    yield return new TestCaseData(config, index);
                }
            }
        }

        [TestCaseSource(nameof(ChainSpecRunnerTests))]
        [Timeout(300000)] // just to make sure we are not on infinite loop on steps because of incorrect dependencies
        public async Task Smoke(string chainSpecPath, int testIndex)
        {
            await SmokeTest(chainSpecPath, testIndex, 30330);
        }
        
        [TestCaseSource(nameof(ChainSpecRunnerTests))]
        [Timeout(30000)] // just to make sure we are not on infinite loop on steps because of incorrect dependencies
        public async Task Smoke_cancel(string chainSpecPath, int testIndex)
        {
            await SmokeTest(chainSpecPath, testIndex, 30430, true);
        }

        private static async Task SmokeTest(string chainSpecPath, int testIndex, int basePort, bool cancel = false)
        {
            Type type1 = typeof(ITxPoolConfig);
            Type type2 = typeof(INetworkConfig);
            Type type3 = typeof(IKeyStoreConfig);
            Type type4 = typeof(IDbConfig);
            Type type5 = typeof(IStatsConfig);
            Type type6 = typeof(IKafkaConfig);
            Type type7 = typeof(IEthStatsConfig);
            Type type8 = typeof(ISyncConfig);
            Type type9 = typeof(IBloomConfig);

            var configProvider = new ConfigProvider();
            configProvider.AddSource(new JsonConfigSource(chainSpecPath));

            Console.WriteLine(type1.Name);
            Console.WriteLine(type2.Name);
            Console.WriteLine(type3.Name);
            Console.WriteLine(type4.Name);
            Console.WriteLine(type5.Name);
            Console.WriteLine(type6.Name);
            Console.WriteLine(type7.Name);
            Console.WriteLine(type8.Name);
            Console.WriteLine(type9.Name);

            var tempPath = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid());
            Directory.CreateDirectory(tempPath);

            Exception exception = null;
            try
            {
                IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
                initConfig.BaseDbPath = tempPath;
                initConfig.ChainSpecPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, initConfig.ChainSpecPath);

                INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
                int port = basePort + testIndex;
                networkConfig.P2PPort = port;
                networkConfig.DiscoveryPort = port;

                INethermindApi nethermindApi = new ApiBuilder(configProvider, TestLogManager.Instance).Create();
                nethermindApi.RpcModuleProvider = new RpcModuleProvider(new FileSystem(), new JsonRpcConfig(), TestLogManager.Instance);
                EthereumRunner runner = new EthereumRunner(nethermindApi);

                using CancellationTokenSource cts = new CancellationTokenSource();
                
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
                        if (exception != null)
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
                    Directory.Delete(tempPath, true);
                }
                catch (Exception e)
                {
                    if (exception != null)
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
    }
}
