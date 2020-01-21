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
using System.Threading.Tasks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Db.Config;
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
using Nethermind.WebSockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    public class EthereumRunnerTest
    {
        public class ConfigSource : IConfigSource
        {
            public (bool IsSet, object Value) GetValue(Type type, string category, string name)
            {
                if (name == "ChainSpecPath")
                {
                    return (true, "testspec.json");
                }

                if (name == "UseMemDb")
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
        
        [Test]
        public async Task Smoke()
        {
            Type type1 = typeof(ITxPoolConfig);
            Type type2 = typeof(INetworkConfig);
            Type type3 = typeof(IKeyStoreConfig);
            Type type4 = typeof(IDbConfig);
            Type type5 = typeof(IStatsConfig);
            Type type6 = typeof(IKafkaConfig);
            Type type7 = typeof(IEthStatsConfig);

            var configProvider = new ConfigProvider();
            configProvider.AddSource(new ConfigSource());
            
            Console.WriteLine(type1.Name);
            Console.WriteLine(type2.Name);
            Console.WriteLine(type3.Name);
            Console.WriteLine(type4.Name);
            Console.WriteLine(type5.Name);
            Console.WriteLine(type6.Name);
            Console.WriteLine(type7.Name);
            
            EthereumRunner runner = new EthereumRunner(
                new RpcModuleProvider(new JsonRpcConfig(), LimboLogs.Instance),
                configProvider,
                LimboLogs.Instance,
                Substitute.For<IGrpcServer>(),
                Substitute.For<INdmConsumerChannelManager>(),
                Substitute.For<INdmDataPublisher>(),
                Substitute.For<INdmInitializer>(),
                Substitute.For<IWebSocketsManager>(),
                new EthereumJsonSerializer(), Substitute.For<IMonitoringService>());

            await runner.Start();
            await runner.StopAsync();
        }
    }
}