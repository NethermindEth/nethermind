using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Discovery.Lifecycle;
using Nevermind.Discovery.Messages;
using Nevermind.Discovery.RoutingTable;
using Nevermind.Discovery.Serializers;
using Nevermind.Json;
using Nevermind.KeyStore;
using Nevermind.Network;

namespace Nevermind.Discovery.Console
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
