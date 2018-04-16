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
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Serializers;
using Nethermind.KeyStore;
using Nethermind.Network;
using Nethermind.Network.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx.Handshake;
using NSubstitute;
using PingMessageSerializer = Nethermind.Discovery.Serializers.PingMessageSerializer;
using PongMessageSerializer = Nethermind.Discovery.Serializers.PongMessageSerializer;

namespace Nethermind.Discovery.Console
{
    class Program
    {
        private static readonly PrivateKey PrivateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        private static readonly ILogger Logger = new NlogLogger();
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
            var privateKeyProvider = new PrivateKeyProvider(PrivateKey);
            var config = new DiscoveryConfigurationProvider(new NetworkHelper(Logger));
            var signer = new Signer();
            var cryptoRandom = new CryptoRandom();
            var configProvider = new ConfigurationProvider();

            var nodeFactory = new NodeFactory();
            var calculator = new NodeDistanceCalculator(config);

            var nodeTable = new NodeTable(config, nodeFactory, new FileKeyStore(configProvider, new JsonSerializer(Logger), new AesEncrypter(configProvider, Logger), cryptoRandom, Logger), Logger, calculator);

            var evictionManager = new EvictionManager(nodeTable, Logger);
            var lifecycleFactory = new NodeLifecycleManagerFactory(nodeFactory, nodeTable, Logger, config, new DiscoveryMessageFactory(config), evictionManager);

            var discoveryManager = new DiscoveryManager(Logger, config, lifecycleFactory, nodeFactory, nodeTable);

            var nodesLocator = new NodesLocator(nodeTable, discoveryManager, config, Logger);
            var discoveryMesageFactory = new DiscoveryMessageFactory(config);
            var nodeIdResolver = new NodeIdResolver(signer);

            var pingSerializer = new PingMessageSerializer(signer, privateKeyProvider, discoveryMesageFactory, nodeIdResolver, nodeFactory);
            var pongSerializer = new PongMessageSerializer(signer, privateKeyProvider, discoveryMesageFactory, nodeIdResolver, nodeFactory);
            var findNodeSerializer = new FindNodeMessageSerializer(signer, privateKeyProvider, discoveryMesageFactory, nodeIdResolver, nodeFactory);
            var neighborsSerializer = new NeighborsMessageSerializer(signer, privateKeyProvider, discoveryMesageFactory, nodeIdResolver, nodeFactory);

            var messageSerializationService = new MessageSerializationService();
            messageSerializationService.Register(pingSerializer);
            messageSerializationService.Register(pongSerializer);
            messageSerializationService.Register(findNodeSerializer);
            messageSerializationService.Register(neighborsSerializer);


            //P2P initialization
            IMessagePad eip8Pad = new Eip8MessagePad(cryptoRandom);
            messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            messageSerializationService.Register(new HelloMessageSerializer());
            messageSerializationService.Register(new DisconnectMessageSerializer());
            messageSerializationService.Register(new Nethermind.Network.P2P.PingMessageSerializer());
            messageSerializationService.Register(new Nethermind.Network.P2P.PongMessageSerializer());
            messageSerializationService.Register(new StatusMessageSerializer());
            IEciesCipher eciesCipher = new EciesCipher(cryptoRandom);
            IEncryptionHandshakeService encryptionHandshakeService = new EncryptionHandshakeService(messageSerializationService, eciesCipher, cryptoRandom, signer, PrivateKey, Logger);
            var p2pManager = new P2PManager(encryptionHandshakeService, Logger, messageSerializationService, Substitute.For<ISynchronizationManager>());
            
            //Connect discovery with P2P
            discoveryManager.RegisterDiscoveryListener(p2pManager);

            _discoveryApp = new DiscoveryApp(config, nodesLocator, Logger, discoveryManager, nodeFactory, nodeTable, messageSerializationService, cryptoRandom);
            _discoveryApp.Start(PrivateKey.PublicKey);
        }

        private static void Stop()
        {
            _discoveryApp.StopAsync();
        }
    }
}
