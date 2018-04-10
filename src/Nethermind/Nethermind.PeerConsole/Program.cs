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
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Evm;
using Nethermind.Mining;
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
        private const int ListenPort = 30309;

        private static PrivateKey _privateKey;

#pragma warning disable 1998
        public static async Task Main(params string[] args)
#pragma warning restore 1998
        {
            // https://msdn.microsoft.com/en-us/magazine/mt763239.aspx
            CommandLineApplication commandLineApplication =
                new CommandLineApplication(throwOnUnexpectedArg: true);
            
            CommandArgument privateKeyOption = commandLineApplication.Argument(
                "key",
                "PrivateKey hex value for this node.");
            
            CommandArgument chainSpecOption = commandLineApplication.Argument(
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
                
                await Run(chainSpecOption.Value, new PrivateKey(privateKeyOption.Value));
                return 0;
            });
            
            commandLineApplication.Execute(args);
        }

        private static async Task Run(string chainSpecFile, PrivateKey privateKey)
        {
            _privateKey = privateKey;
            await ConnectTestnet(chainSpecFile);
        }

        private static async Task ConnectTestnet(string chainSpecFile)
        {
            ICryptoRandom cryptoRandom = new CryptoRandom();
            Console.WriteLine($"Local node ID = {_privateKey.PublicKey}");

            /* tools */
            var signer = new Signer();
            var logger = new ConsoleLogger();
            
            /* network */
            var serializationService = new MessageSerializationService();

            /* rlpx */
            var eciesCipher = new EciesCipher(cryptoRandom);
            var eip8Pad = new Eip8MessagePad(cryptoRandom);
            serializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            serializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            var encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _privateKey, logger);
            
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
           
            /* spec */
            var sealEngine = new EthashSealEngine(new Ethash());
            var specProvider = RopstenSpecProvider.Instance;
            var calculator = new DifficultyCalculator(specProvider);
            
            /* sync */
            var transactionStore = new TransactionStore();
            var blockStore = new BlockStore();
            
            /* validation */
            var headerValidator = new HeaderValidator(calculator, blockStore, sealEngine, specProvider, logger);
            var ommersValidator = new OmmersValidator(blockStore, headerValidator, logger);
            var transactionValidator = new TransactionValidator(new SignatureValidator(ChainId.Ropsten));
            var blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, specProvider, logger);

            /* state */
            var dbProvider = new DbProvider(logger);
            var codeDb = new InMemoryDb();
            var stateDb = new InMemoryDb();
            var stateTree = new StateTree(stateDb);
            var stateProvider = new StateProvider(stateTree, logger, codeDb);
            var storageProvider = new StorageProvider(dbProvider, stateProvider, logger);
            
            /* blockchain */
            var blockTree = new BlockTree();
            var blockhashProvider = new BlockhashProvider(blockTree);
            var rewardCalculator = new RewardCalculator(specProvider);
            
            var ethereumSigner = new EthereumSigner(specProvider, logger);
            var virtualMachine = new VirtualMachine(specProvider, stateProvider, storageProvider, blockhashProvider, logger);
            var transactionProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, ethereumSigner, logger);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, transactionProcessor, dbProvider, stateProvider, storageProvider, transactionStore, logger);
            var blockchainProcessor = new BlockchainProcessor(blockTree, NullSealEngine.Instance, transactionStore, calculator, blockProcessor, logger);
            
            /* genesis */
            var loader = new ChainSpecLoader(new UnforgivingJsonSerializer()); 
            if (!Path.IsPathRooted(chainSpecFile))
            {
                chainSpecFile = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Chains", chainSpecFile));
            }

            logger.Log($"Loading ChainSpec from {chainSpecFile}");
            var chainSpec = loader.Load(File.ReadAllBytes(chainSpecFile));
            foreach (KeyValuePair<Address, BigInteger> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }
            
            stateProvider.Commit(specProvider.GetGenesisSpec());
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot;
            chainSpec.Genesis.Header.RecomputeHash();
            var expectedGenesisHash = "0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d";
            if (chainSpec.Genesis.Hash != new Keccak(expectedGenesisHash))
            {
                throw new Exception($"Unexpected genesis hash for Ropsten, expected {expectedGenesisHash}, but was {chainSpec.Genesis.Hash}");
            }
            
            blockchainProcessor.Start(chainSpec.Genesis);
            var synchronizationManager = new SynchronizationManager(headerValidator, blockValidator, transactionValidator, specProvider, chainSpec.Genesis, logger);

            logger.Log("Initializing server...");
            var localPeer = new RlpxPeer(_privateKey.PublicKey, ListenPort, encryptionHandshakeServiceA, serializationService, synchronizationManager, logger);
            await Task.WhenAll(localPeer.Init());
            
            // https://stackoverflow.com/questions/6803073/get-local-ip-address?utm_medium=organic&utm_source=google_rich_qa&utm_campaign=google_rich_qa
            string localIp;
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                Debug.Assert(endPoint != null, "null endpoint when connecting a UDP socket");
                localIp = endPoint.Address.ToString();
            }
            
            logger.Log($"Node is up and listening on {localIp}:{ListenPort}... press ENTER to exit");
            logger.Log($"enode://{_privateKey.PublicKey.ToString(false)}@{localIp}:{ListenPort}");

            if (chainSpec.Bootnodes.Any())
            {
                Bootnode bootnode = chainSpec.Bootnodes[0];
                logger.Log($"Connecting to testnet bootnode {bootnode.Description}");
                await localPeer.ConnectAsync(bootnode.PublicKey, bootnode.Host, bootnode.Port);
                logger.Log("Testnet connected...");
            }
            
            Console.ReadLine();
            logger.Log("Shutting down...");
            await Task.WhenAll(localPeer.Shutdown());
            logger.Log("Goodbye...");
        }
    }
}