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
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
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
using NSubstitute;
using ILogger = Nethermind.Core.ILogger;

namespace Nethermind.PeerConsole
{
    internal static class Program
    {
        private const int PortA = 8001;
        private const int PortB = 8002;
        private const int PortC = 8003;

        private static PrivateKey _keyA;
        private static PrivateKey _keyB;
        private static PrivateKey _keyC;

        public static async Task Main(string[] args)
        {
            await Run();
        }

        private static async Task Run()
        {
            //await ConnectLocal();
            await ConnectTestnet();
        }

        private static async Task ConnectLocal()
        {
            ICryptoRandom cryptoRandom = new CryptoRandom();
            _keyA = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            _keyB = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            _keyC = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));

            ISigner signer = new Signer();
            ILogger logger = new ConsoleLogger();
            IEciesCipher eciesCipher = new EciesCipher(cryptoRandom);
            IMessageSerializationService serializationService = new MessageSerializationService();

            IMessagePad eip8Pad = new Eip8MessagePad(cryptoRandom);
            serializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            serializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            serializationService.Register(new HelloMessageSerializer());
            serializationService.Register(new DisconnectMessageSerializer());
            serializationService.Register(new PingMessageSerializer());
            serializationService.Register(new PongMessageSerializer());
            serializationService.Register(new StatusMessageSerializer());

            
            Block block = new Block(new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131200, 1, 100, 1, new byte[0]));
            BlockStore blockStore = new BlockStore();
            blockStore.AddBlock(block);
            block.Header.RecomputeHash();
            IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
            IDifficultyCalculator calculator = new DifficultyCalculator(RopstenSpecProvider.Instance); // dynamic spec here will be broken
            ITransactionStore transactionStore = new TransactionStore();
            BlockTree blockTree = new BlockTree();
            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockTree, NullSealEngine.Instance, transactionStore, calculator, blockProcessor, logger);

            IEncryptionHandshakeService encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyA, logger);
            IEncryptionHandshakeService encryptionHandshakeServiceB = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyB, logger);
            //            IEncryptionHandshakeService encryptionHandshakeServiceC = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyC, logger);

            Console.WriteLine("Initializing server...");
            RlpxPeer peerServerA = new RlpxPeer(_keyA.PublicKey, PortA, encryptionHandshakeServiceA, serializationService, Substitute.For<ISynchronizationManager>(), logger);
            RlpxPeer peerServerB = new RlpxPeer(_keyB.PublicKey, PortB, encryptionHandshakeServiceB, serializationService, Substitute.For<ISynchronizationManager>(), logger);
            //            RlpxPeer peerServerC = new RlpxPeer(serializationService, encryptionHandshakeServiceC, sessionFactoryC, logger);
            //            await Task.WhenAll(peerServerA.Init(PortA), peerServerB.Init(PortB), peerServerC.Init(PortC));
            //            await Task.WhenAll(peerServerA.Init(PortA), peerServerB.Init(PortB), peerServerC.Init(PortC));
            await Task.WhenAll(peerServerA.Init(), peerServerB.Init());
            Console.WriteLine("Servers running...");
            Console.WriteLine("Connecting A to B...");
            await peerServerA.ConnectAsync(_keyB.PublicKey, "127.0.0.1", PortB);
            Console.WriteLine("A to B connected...");
            //            Console.WriteLine("Connecting A to C...");
            //            await peerServerA.ConnectAsync(_keyC.PublicKey, "127.0.0.1", PortC);
            //            Console.WriteLine("A to C connected...");
            //            await peerServerB.ConnectAsync(_keyA.PublicKey, "localhost", PortA);
            Console.ReadLine();
            Console.WriteLine("Shutting down...");
            await Task.WhenAll(peerServerA.Shutdown(), peerServerB.Shutdown());
            Console.WriteLine("Goodbye...");
        }

        private static async Task ConnectTestnet()
        {
            Bootnode bootnode = Bootnodes.EthJ[3];

            ICryptoRandom cryptoRandom = new CryptoRandom();
//            _keyA = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
            _keyA = new PrivateKey("000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");
            Console.WriteLine($"Local node ID = {_keyA.PublicKey}");

            ISigner signer = new Signer();
            ILogger logger = new ConsoleLogger();
            IEciesCipher eciesCipher = new EciesCipher(cryptoRandom);
            IMessageSerializationService serializationService = new MessageSerializationService();

            IMessagePad eip8Pad = new Eip8MessagePad(cryptoRandom);
            serializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            serializationService.Register(new AckEip8MessageSerializer(eip8Pad));
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
            serializationService.Register(new NewBlockHashesMessageSerializer());
            serializationService.Register(new NewBlockHashesMessageSerializer());
           
            IReleaseSpec dynamicSpecForVm = new DynamicReleaseSpec(RopstenSpecProvider.Instance);
            ISealEngine sealEngine = new EthashSealEngine(new Ethash());
            
            IBlockStore blockStore = new BlockStore();
            IDifficultyCalculator calculator = new DifficultyCalculator(RopstenSpecProvider.Instance); // dynamic spec here will be broken
            IHeaderValidator headerValidator = new HeaderValidator(calculator, blockStore, sealEngine, RopstenSpecProvider.Instance, logger);
            IOmmersValidator ommersValidator = new OmmersValidator(blockStore, headerValidator, logger);
            IReleaseSpec currentSpec = RopstenSpecProvider.Instance.GetCurrentSpec();
            ITransactionValidator transactionValidator = new TransactionValidator(currentSpec, new SignatureValidator(currentSpec, ChainId.Ropsten));
            IBlockValidator blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, logger);

            BlockTree blockTree = new BlockTree();
            IBlockhashProvider blockhashProvider = new BlockhashProvider(blockTree);
            IDbProvider dbProvider = new DbProvider(logger);
            InMemoryDb codeDb = new InMemoryDb();
            InMemoryDb stateDb = new InMemoryDb();
            StateTree stateTree = new StateTree(stateDb);
            IStateProvider stateProvider = new StateProvider(stateTree, dynamicSpecForVm, logger, codeDb);
            IStorageProvider storageProvider = new StorageProvider(dbProvider, stateProvider, logger);
            
            IRewardCalculator rewardCalculator = new RewardCalculator(RopstenSpecProvider.Instance);
            IVirtualMachine virtualMachine = new VirtualMachine(dynamicSpecForVm, stateProvider, storageProvider, blockhashProvider, logger);
            IEthereumSigner ethereumSigner = new EthereumSigner(RopstenSpecProvider.Instance, logger);
            ITransactionProcessor processor = new TransactionProcessor(dynamicSpecForVm, stateProvider, storageProvider, virtualMachine, ethereumSigner, logger);
            ITransactionStore transactionStore = new TransactionStore();
            IBlockProcessor blockProcessor = new BlockProcessor(RopstenSpecProvider.Instance, blockValidator, rewardCalculator, processor, dbProvider, stateProvider, storageProvider, transactionStore, logger);
            IBlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockTree, NullSealEngine.Instance, transactionStore, calculator, blockProcessor, logger);
            
            ChainSpecLoader loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            string path = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\Chains", "ropsten.json"));
            logger.Log($"Loading ChainSpec from {path}");
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            foreach (KeyValuePair<Address, BigInteger> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }
            
            stateProvider.Commit();
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot;
            chainSpec.Genesis.Header.RecomputeHash();
            if (chainSpec.Genesis.Hash != new Keccak("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"))
            {
                throw new Exception("Unexpected genesis hash");
            }
            
            blockchainProcessor.Start(chainSpec.Genesis);
            
            ISynchronizationManager synchronizationManager = new SynchronizationManager(headerValidator, blockValidator, transactionValidator, chainSpec.Genesis, logger);
            IEncryptionHandshakeService encryptionHandshakeServiceA = new EncryptionHandshakeService(serializationService, eciesCipher, cryptoRandom, signer, _keyA, logger);

            logger.Log("Initializing server...");
            RlpxPeer localPeer = new RlpxPeer(_keyA.PublicKey, PortA, encryptionHandshakeServiceA, serializationService, synchronizationManager, logger);
            await Task.WhenAll(localPeer.Init());
            logger.Log("Servers running...");
            logger.Log($"Connecting to testnet bootnode {bootnode.Description}");
            await localPeer.ConnectAsync(bootnode.PublicKey, bootnode.Host, bootnode.Port);
            logger.Log("Testnet connected...");
            Console.ReadLine();
            logger.Log("Shutting down...");
            await Task.WhenAll(localPeer.Shutdown());
            logger.Log("Goodbye...");
        }
    }
}