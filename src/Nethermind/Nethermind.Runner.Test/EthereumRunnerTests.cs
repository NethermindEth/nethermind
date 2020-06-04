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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net.Core;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.EthStats;
using Nethermind.Grpc;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network.Config;
using Nethermind.PubSub.Kafka;
using Nethermind.Runner.Ethereum;
using Nethermind.Serialization.Json;
using Nethermind.Stats;
using Nethermind.Db.Blooms;
using Nethermind.TxPool;
using Nethermind.WebSockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    [TestFixture, Parallelizable(ParallelScope.Self)]
    public class EthereumRunnerTests
    {
        public class ConfigSource : IConfigSource
        {
            private readonly string _chainspecPath;

            public ConfigSource(string chainspecPath)
            {
                _chainspecPath = chainspecPath;
            }
            
            public (bool IsSet, object Value) GetValue(Type type, string category, string name)
            {
                if (name == "ChainSpecPath")
                {
                    return (true, _chainspecPath);
                }

                if (name == "UseMemDb")
                {
                    return (true, true);
                }
                
                if (name.EndsWith("Enabled"))
                {
                    return (true, true);
                }
                
                return (false, null);
            }

            public (bool IsSet, string Value) GetRawValue(string category, string name)
            {
                return (false, null);
            }
        }

        public static IEnumerable ChainSpecRunnerTests
        {
            get
            {
                ISet<string> ignoredSpecs = new HashSet<string>()
                {
                    "genesis.json",
                    "ndm.json",
                    "hive.json",
                    "ndmtestnet0.json",
                    "ndmtestnet0_local.json",
                };
                yield return new TestCaseData("testspec.json");
                
                foreach (var config in Directory.GetFiles("chainspec").Where(c => !ignoredSpecs.Contains(Path.GetFileName(c))))
                {
                    yield return new TestCaseData(config);
                }
            }
        }
        
        [TestCaseSource(nameof(ChainSpecRunnerTests))]
        [Timeout(12000)] // just to make sure we are not on infinite loop on steps because of incorrect dependencies
        public async Task Smoke(string chainSpecPath)
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
            configProvider.AddSource(new ConfigSource(chainSpecPath));
            
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
            
            try
            {
                configProvider.GetConfig<IInitConfig>().BaseDbPath = tempPath;
            
                EthereumRunner runner = new EthereumRunner(
                    new RpcModuleProvider(new JsonRpcConfig(), LimboLogs.Instance),
                    configProvider,
                    NUnitLogManager.Instance, 
                    Substitute.For<IGrpcServer>(),
                    Substitute.For<INdmConsumerChannelManager>(),
                    Substitute.For<INdmDataPublisher>(),
                    Substitute.For<INdmInitializer>(),
                    Substitute.For<IWebSocketsManager>(),
                    new EthereumJsonSerializer(), 
                    Substitute.For<IMonitoringService>());

                await runner.Start(CancellationToken.None);
                await runner.StopAsync();
            }
            finally
            {
                // rocks db still has a lock on a file called "LOCK".
                Directory.Delete(tempPath, true);
            }
        }
        
                [TestCaseSource(nameof(ChainSpecRunnerTests))]
        [Timeout(12000)] // just to make sure we are not on infinite loop on steps because of incorrect dependencies
        public async Task Smoke_cancel(string chainSpecPath)
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
            configProvider.AddSource(new ConfigSource(chainSpecPath));
            
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
            
            try
            {
                configProvider.GetConfig<IInitConfig>().BaseDbPath = tempPath;
            
                EthereumRunner runner = new EthereumRunner(
                    new RpcModuleProvider(new JsonRpcConfig(), LimboLogs.Instance),
                    configProvider,
                    NUnitLogManager.Instance, 
                    Substitute.For<IGrpcServer>(),
                    Substitute.For<INdmConsumerChannelManager>(),
                    Substitute.For<INdmDataPublisher>(),
                    Substitute.For<INdmInitializer>(),
                    Substitute.For<IWebSocketsManager>(),
                    new EthereumJsonSerializer(), 
                    Substitute.For<IMonitoringService>());

                CancellationTokenSource cts = new CancellationTokenSource();
                Task task = runner.Start(cts.Token);

                cts.Cancel();
                
                await task;
            }
            finally
            {
                // rocks db still has a lock on a file called "LOCK".
                Directory.Delete(tempPath, true);
            }
        }
    }
}