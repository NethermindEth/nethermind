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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Evm;
using Nethermind.Network;
using Nethermind.Network.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Store;

namespace Nethermind.PeerConsole
{
    internal static class Program
    {
        private static int _listenPort = 30312;

        private static PrivateKey _privateKey;
        
        private static readonly ILogger Logger = new ConsoleLogger();

#pragma warning disable 1998
        public static async Task Main(params string[] args)
#pragma warning restore 1998
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                Console.WriteLine(eventArgs.ExceptionObject);
            };
            
            // https://msdn.microsoft.com/en-us/magazine/mt763239.aspx
            CommandLineApplication commandLineApplication =
                new CommandLineApplication(throwOnUnexpectedArg: true);
            
            CommandArgument keyArg = commandLineApplication.Argument(
                "key",
                "PrivateKey hex value for this node.");
            
            CommandArgument portArg = commandLineApplication.Argument(
                "port",
                "Listen port");
            
            CommandArgument chainSpecArg = commandLineApplication.Argument(
                "specfile",
                "ChainSpec file name (from Chains directory)");
            
            commandLineApplication.HelpOption("-?|-h|--help");
            
            commandLineApplication.OnExecute(async () =>
            {                  
//                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
//                {
//                    _privateKey = new PrivateKey("000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
//                }
//                else
//                {
//                    _privateKey = new PrivateKey("010102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
//                }
                
                await Run(chainSpecArg.Value, int.Parse(portArg.Value), new PrivateKey(keyArg.Value));
                return 0;
            });
            
            commandLineApplication.Execute(args);
        }

        private static async Task Run(string chainSpecFile, int port, PrivateKey privateKey)
        {
            _listenPort = port;
            _privateKey = privateKey;
            await InitTestnet(chainSpecFile);
        }

        private static async Task InitTestnet(string chainSpecFile)
        {
            /* tools */
            
            var chainSpec = LoadChainSpec(chainSpecFile);
            InitBlockchain(chainSpec);            
            await InitNet(chainSpec);
        }

        private static ISynchronizationManager _syncManager;
        private static IBlockchainProcessor _blockchainProcessor;

        private static ChainSpec LoadChainSpec(string chainSpecFile)
        {
            Logger.Log($"Loading ChainSpec from {chainSpecFile}");
            var loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            if (!Path.IsPathRooted(chainSpecFile))
            {
                chainSpecFile = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chains", chainSpecFile));
            }

            var chainSpec = loader.Load(File.ReadAllBytes(chainSpecFile));
            return chainSpec;
        }

        private static void InitBlockchain(ChainSpec chainSpec)
        {
            /* spec */
            var blockMiningTime = TimeSpan.FromMilliseconds(10000);
            var transactionDelay = TimeSpan.FromMilliseconds(10000);
            var specProvider = RopstenSpecProvider.Instance;
            var difficultyCalculator = new DifficultyCalculator(specProvider);
            // var sealEngine = new EthashSealEngine(new Ethash());
            var sealEngine = new FakeSealEngine(blockMiningTime);

            /* sync */
            var transactionStore = new TransactionStore();
            var blockTree = new BlockTree();

            /* validation */
            var headerValidator = new HeaderValidator(difficultyCalculator, blockTree, sealEngine, specProvider, Logger);
            var ommersValidator = new OmmersValidator(blockTree, headerValidator, Logger);
            var txValidator = new TransactionValidator(new SignatureValidator(ChainId.Ropsten));
            var blockValidator = new BlockValidator(txValidator, headerValidator, ommersValidator, specProvider, Logger);

            /* state */
            var dbProvider = new DbProvider(Logger);
            var codeDb = new InMemoryDb();
            var stateDb = new InMemoryDb();
            var stateTree = new StateTree(stateDb);
            var stateProvider = new StateProvider(stateTree, Logger, codeDb);
            var storageProvider = new StorageProvider(dbProvider, stateProvider, Logger);
            var storageDbProvider = new DbProvider(Logger);

            /* blockchain */
            var ethereumSigner = new EthereumSigner(specProvider, Logger);

            /* blockchain processing */
            var blockhashProvider = new BlockhashProvider(blockTree);
            var virtualMachine = new VirtualMachine(specProvider, stateProvider, storageProvider, blockhashProvider, Logger);
            var transactionProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, ethereumSigner, Logger);
            var rewardCalculator = new RewardCalculator(specProvider);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, transactionProcessor, storageDbProvider, stateProvider, storageProvider, transactionStore, Logger);
            _blockchainProcessor = new BlockchainProcessor(blockTree, sealEngine, transactionStore, difficultyCalculator, blockProcessor, Logger);

            /* genesis */
            foreach (KeyValuePair<Address, BigInteger> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }
            stateProvider.Commit(specProvider.GenesisSpec);
            
            var testTransactionsGenerator = new TestTransactionsGenerator(transactionStore, ethereumSigner, transactionDelay, Logger);
            stateProvider.CreateAccount(testTransactionsGenerator.SenderAddress, 1000.Ether());
            stateProvider.Commit(specProvider.GenesisSpec);
            testTransactionsGenerator.Start();
            
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot;
            chainSpec.Genesis.Header.RecomputeHash();
            // we are adding test transactions account so the state root will change (not an actual ropsten at the moment)
//            var expectedGenesisHash = "0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d";
//            if (chainSpec.Genesis.Hash != new Keccak(expectedGenesisHash))
//            {
//                throw new Exception($"Unexpected genesis hash for Ropsten, expected {expectedGenesisHash}, but was {chainSpec.Genesis.Hash}");
//            }

            /* start test processing */
            sealEngine.IsMining = true;
            blockTree.AddBlock(chainSpec.Genesis);
            _blockchainProcessor.Start(chainSpec.Genesis);

            var bestBlock = chainSpec.Genesis;
            var totalDifficulty = chainSpec.Genesis.Header.Difficulty;
            _syncManager = new SynchronizationManager(headerValidator, blockValidator, txValidator, specProvider, bestBlock, totalDifficulty, Logger);
            _syncManager.Start();
        }

        private static async Task InitNet(ChainSpec chainSpec)
        {
            /* tools */
            var serializationService = new MessageSerializationService();
            var cryptoRandom = new CryptoRandom();
            var signer = new Signer();

            /* rlpx */
            var eciesCipher = new EciesCipher(cryptoRandom);
            var eip8Pad = new Eip8MessagePad(cryptoRandom);
            serializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            serializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            var encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _privateKey, Logger);

            /* p2p */
            serializationService.Register(new HelloMessageSerializer());
            serializationService.Register(new DisconnectMessageSerializer());
            serializationService.Register(new PingMessageSerializer());
            serializationService.Register(new PongMessageSerializer());

            /* eth */
            serializationService.Register(new StatusMessageSerializer());
            serializationService.Register(new TransactionsMessageSerializer());
            serializationService.Register(new GetBlockHeadersMessageSerializer());
            serializationService.Register(new NewBlockHashesMessageSerializer());
            serializationService.Register(new GetBlockBodiesMessageSerializer());
            serializationService.Register(new BlockHeadersMessageSerializer());
            serializationService.Register(new BlockBodiesMessageSerializer());
            serializationService.Register(new NewBlockMessageSerializer());

            Logger.Log("Initializing server...");
            var localPeer = new RlpxPeer(_privateKey.PublicKey, _listenPort, encryptionHandshakeServiceA, serializationService, _syncManager, Logger);
            await localPeer.Init();

            // https://stackoverflow.com/questions/6803073/get-local-ip-address?utm_medium=organic&utm_source=google_rich_qa&utm_campaign=google_rich_qa
            string localIp;
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                Debug.Assert(endPoint != null, "null endpoint when connecting a UDP socket");
                localIp = endPoint.Address.ToString();
            }

            Logger.Log($"Node is up and listening on {localIp}:{_listenPort}... press ENTER to exit");
            Logger.Log($"enode://{_privateKey.PublicKey.ToString(false)}@{localIp}:{_listenPort}");

            if (chainSpec.Bootnodes.Any())
            {
                Bootnode bootnode = chainSpec.Bootnodes[0];
                Logger.Log($"Connecting to testnet bootnode {bootnode.Description}");
                await localPeer.ConnectAsync(bootnode.PublicKey, bootnode.Host, bootnode.Port);
                Logger.Log("Testnet connected...");
            }

            Console.ReadLine();
            Logger.Log("Shutting down...");
            Logger.Log("Stopping sync manager...");
            await _syncManager.StopAsync();
            Logger.Log("Stopping blockchain processor...");
            await _blockchainProcessor.StopAsync();
            Logger.Log("Stopping local peer...");
            await localPeer.Shutdown();
            Logger.Log("Goodbye...");
        }
    }
}