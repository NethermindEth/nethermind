// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
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
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using BlockTree = Nethermind.Blockchain.BlockTree;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Synchronization.ParallelSync;

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

            WorldState stateProvider = new(trieStore, codeDb, LimboLogs.Instance);
            stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            stateProvider.Commit(spec);
            stateProvider.CommitTree(0);

            StateReader stateReader = new(trieStore, codeDb, LimboLogs.Instance);

            ChainLevelInfoRepository chainLevelInfoRepository = new(blockInfoDb);
            BlockTree blockTree = new(dbProvider, chainLevelInfoRepository, specProvider, NullBloomStorage.Instance, LimboLogs.Instance);
            _blockhashProvider = new BlockhashProvider(blockTree, LimboLogs.Instance);
            _virtualMachine = new VirtualMachine(_blockhashProvider, specProvider, LimboLogs.Instance);

            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            blockTree.SuggestBlock(genesisBlock);

            Block block1 = Build.A.Block.WithParent(genesisBlock).WithNumber(1).TestObject;
            blockTree.SuggestBlock(block1);

            TransactionProcessor transactionProcessor
                 = new(MainnetSpecProvider.Instance, stateProvider, _virtualMachine, LimboLogs.Instance);

            IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor = new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider);
            BlockProcessor blockProcessor = new(specProvider, Always.Valid, new RewardCalculator(specProvider), transactionsExecutor,
                stateProvider, NullReceiptStorage.Instance, NullWitnessCollector.Instance, LimboLogs.Instance);

            EthereumEcdsa ecdsa = new(specProvider.ChainId, LimboLogs.Instance);
            BlockchainProcessor blockchainProcessor = new(
                blockTree,
                blockProcessor,
                new RecoverSignatures(
                    ecdsa,
                    NullTxPool.Instance,
                    specProvider,
                    LimboLogs.Instance),
                stateReader,
                LimboLogs.Instance,
                BlockchainProcessor.Options.NoReceipts);

            blockchainProcessor.Process(genesisBlock, ProcessingOptions.None, NullBlockTracer.Instance);
            blockchainProcessor.Process(block1, ProcessingOptions.None, NullBlockTracer.Instance);

            IBloomStorage bloomStorage = new BloomStorage(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());

            LogFinder logFinder = new(
                blockTree,
                new InMemoryReceiptStorage(),
                new InMemoryReceiptStorage(),
                bloomStorage,
                LimboLogs.Instance,
                new ReceiptsRecovery(ecdsa, specProvider));

            BlockchainBridge bridge = new(
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
                new BlocksConfig(),
                false);

            GasPriceOracle gasPriceOracle = new(blockTree, specProvider, LimboLogs.Instance);
            FeeHistoryOracle feeHistoryOracle = new(blockTree, NullReceiptStorage.Instance, specProvider);

            IReceiptStorage receiptStorage = new InMemoryReceiptStorage();
            ISyncConfig syncConfig = new SyncConfig();
            EthSyncingInfo ethSyncingInfo = new(blockTree, receiptStorage, syncConfig, new StaticSelector(SyncMode.All), LimboLogs.Instance);

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
                gasPriceOracle,
                ethSyncingInfo,
                feeHistoryOracle);
        }

        [Benchmark]
        public void Current()
        {
            _ethModule.eth_getBalance(Address.Zero, new BlockParameter(1));
            _ethModule.eth_getBlockByNumber(new BlockParameter(1), false);
        }
    }
}
