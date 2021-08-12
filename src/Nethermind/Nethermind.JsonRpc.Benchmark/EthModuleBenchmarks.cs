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

using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.JsonRpc.Benchmark
{
    public class EthModuleBenchmarks
    {
        private IVirtualMachine _virtualMachine;
        private IBlockhashProvider _blockhashProvider;
        private EthRpcModule _ethModule;

        [GlobalSetup]
        public void GlobalSetup()
        {
            var dbProvider = TestMemDbProvider.Init();
            IDb codeDb = dbProvider.CodeDb;
            IDb stateDb = dbProvider.StateDb;
            IDb blockInfoDb = new MemDb(10, 5);

            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            IReleaseSpec spec = MainnetSpecProvider.Instance.GenesisSpec;
            var trieStore = new TrieStore(stateDb, LimboLogs.Instance);
            
            StateProvider stateProvider = new StateProvider(trieStore, codeDb, LimboLogs.Instance);
            stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            stateProvider.Commit(spec);

            StorageProvider storageProvider = new StorageProvider(trieStore, stateProvider, LimboLogs.Instance);
            StateReader stateReader = new StateReader(trieStore, codeDb, LimboLogs.Instance);
            
            ChainLevelInfoRepository chainLevelInfoRepository = new ChainLevelInfoRepository(blockInfoDb);
            BlockTree blockTree = new BlockTree(dbProvider, chainLevelInfoRepository, specProvider, NullBloomStorage.Instance, LimboLogs.Instance);
            _blockhashProvider = new BlockhashProvider(blockTree, LimboLogs.Instance);
            _virtualMachine = new VirtualMachine(stateProvider, storageProvider, _blockhashProvider, specProvider, LimboLogs.Instance);

            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            blockTree.SuggestBlock(genesisBlock);
            
            Block block1 = Build.A.Block.WithParent(genesisBlock).WithNumber(1).TestObject;
            blockTree.SuggestBlock(block1);
            
            TransactionProcessor transactionProcessor
                 = new TransactionProcessor(MainnetSpecProvider.Instance, stateProvider, storageProvider, _virtualMachine, LimboLogs.Instance);

            IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor = new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider);
            BlockProcessor blockProcessor = new BlockProcessor(specProvider, Always.Valid, new RewardCalculator(specProvider), transactionsExecutor, 
                stateProvider, storageProvider, NullReceiptStorage.Instance, NullWitnessCollector.Instance, LimboLogs.Instance);

            EthereumEcdsa ecdsa = new EthereumEcdsa(specProvider.ChainId, LimboLogs.Instance);
            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                blockTree,
                blockProcessor,
                new RecoverSignatures(
                    ecdsa,
                    NullTxPool.Instance,
                    specProvider,
                    LimboLogs.Instance),
                LimboLogs.Instance,
                BlockchainProcessor.Options.NoReceipts);

            blockchainProcessor.Process(genesisBlock, ProcessingOptions.None, NullBlockTracer.Instance);
            blockchainProcessor.Process(block1, ProcessingOptions.None, NullBlockTracer.Instance);
            
            IBloomStorage bloomStorage = new BloomStorage(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());

            LogFinder logFinder = new LogFinder(
                blockTree,
                new InMemoryReceiptStorage(),
                bloomStorage,
                LimboLogs.Instance,
                new ReceiptsRecovery(ecdsa, specProvider));
            
            BlockchainBridge bridge = new BlockchainBridge(
                new ReadOnlyTxProcessingEnv(
                    new ReadOnlyDbProvider(dbProvider, false),
                    trieStore.AsReadOnly(),
                    new ReadOnlyBlockTree(blockTree),
                    specProvider,
                    LimboLogs.Instance),
                NullTxPool.Instance,
                NullReceiptStorage.Instance,
                NullFilterStore.Instance,
                NullFilterManager.Instance,
                ecdsa,
                Timestamper.Default,
                logFinder,
                specProvider,
                false,
                false);

            GasPriceOracle gasPriceOracle = new(blockTree, specProvider);
            
            _ethModule = new EthRpcModule(
                new JsonRpcConfig(),
                bridge,
                blockTree,
                stateReader,
                NullTxPool.Instance,
                NullTxSender.Instance,
                NullWallet.Instance,
                LimboLogs.Instance,
                specProvider, 
                gasPriceOracle);
        }

        [Benchmark]
        public void Current()
        {
            _ethModule.eth_getBalance(Address.Zero, new BlockParameter(1));
            _ethModule.eth_getBlockByNumber(new BlockParameter(1), false);
        }
    }
}
