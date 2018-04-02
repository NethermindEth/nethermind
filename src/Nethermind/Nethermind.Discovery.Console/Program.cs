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

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Serializers;
using Nethermind.KeyStore;
using Nethermind.Network;

namespace Nethermind.Discovery.Console
{
    class Program
    {
        private static readonly PrivateKey PrivateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        private static readonly ILogger Logger = new ConsoleLogger();
        private static IDiscoveryApp _discoveryApp;

        static void Main(string[] args)
        {
            try
            {
                Logger.Log("Running DiscoveryConsole app");
                Start();
                System.Console.ReadKey();
                Logger.Log("Stopping DiscoveryConsole app");
                Stop();
            }
            catch (Exception e)
            {
                Logger.Error("Error during discovery run", e);
            }
        }

        private static void Start()
        {
            var config = new DiscoveryConfigurationProvider();
            var signer = new Signer();
            var cryptoRandom = new CryptoRandom();
            var configProvider = new ConfigurationProvider(Path.GetDirectoryName(Path.Combine(Path.GetTempPath(), "KeyStore")));

            var nodeFactory = new NodeFactory();
            var calculator = new NodeDistanceCalculator(config);

            var nodeTable = new NodeTable(config, nodeFactory, new FileKeyStore(configProvider, new JsonSerializer(Logger), new AesEncrypter(configProvider, Logger), cryptoRandom, Logger), Logger, calculator);
            var evictionManager = new EvictionManager(nodeTable, Logger);
            var lifecycleFactory = new NodeLifecycleManagerFactory(nodeFactory, nodeTable, Logger, config, new DiscoveryMessageFactory(config), evictionManager);

            var discoveryManager = new DiscoveryManager(Logger, config, lifecycleFactory, nodeFactory, nodeTable);

            var nodesLocator = new NodesLocator(nodeTable, discoveryManager, config, Logger);
            var discoveryMesageFactory = new DiscoveryMessageFactory(config);
            var nodeIdResolver = new NodeIdResolver(signer);

            var pingSerializer = new PingMessageSerializer(signer, PrivateKey, discoveryMesageFactory, nodeIdResolver, nodeFactory);
            var pongSerializer = new PongMessageSerializer(signer, PrivateKey, discoveryMesageFactory, nodeIdResolver, nodeFactory);
            var findNodeSerializer = new FindNodeMessageSerializer(signer, PrivateKey, discoveryMesageFactory, nodeIdResolver, nodeFactory);
            var neighborsSerializer = new NeighborsMessageSerializer(signer, PrivateKey, discoveryMesageFactory, nodeIdResolver, nodeFactory);

            var messageSerializationService = new MessageSerializationService();
            messageSerializationService.Register(pingSerializer);
            messageSerializationService.Register(pongSerializer);
            messageSerializationService.Register(findNodeSerializer);
            messageSerializationService.Register(neighborsSerializer);

            _discoveryApp = new DiscoveryApp(config, nodesLocator, Logger, discoveryManager, nodeFactory, nodeTable, messageSerializationService, cryptoRandom);
            _discoveryApp.Start();
        }

        private static void Stop()
        {
            _discoveryApp.Stop();
        }
    }
}
