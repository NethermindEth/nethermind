// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Jint.Parser;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Eth.Multicall;

/// <summary>
/// The MultiCallBlockchainFork class enables the creation of temporary in-memory blockchain instances,
/// based on a base chain's state. It's useful for simulating transactions, testing,
/// and executing smart contracts without affecting the base chain. The class offers RPC,
/// StateProvider, and StorageProvider utilities for swift chain data manipulation
/// and supports multi-block chain simulations using CreateBlock/FinaliseBlock pair manually or ForgeChainBlock method.
/// </summary>
public class MultiCallBlockchainFork : IDisposable
{
    private BlockchainProcessor ChainProcessor { get; set; }

    public StateProvider StateProvider { get; internal set; }
    public StorageProvider StorageProvider { get; internal set; }
    public ISpecProvider SpecProvider { get; internal set; }
    public IBlockTree BlockTree { get; set; }
    public IBlockFinder BlockFinder
    {
        get => BlockTree;
    }
    public Block? LatestBlock { get => BlockFinder.Head; }

    public EthRpcModule EthRpcModule { get; internal set; }
    public IReleaseSpec CurrentSpec { get => SpecProvider.GetSpec(LatestBlock!.Header); }

    /// <summary>
    /// Creates a MultiCallBlockchainFork instance with the following steps:
    /// 1. Initialize MultiCallBlockchainFork object
    /// 2. Create read-only in-memory databases
    /// 3. Set spec provider
    /// 4. Set up log manager
    /// 5. Establish EthereumEcdsa
    /// 6. Configure TrieStore, StateProvider, and StorageProvider
    /// 7. Prepare state reader
    /// 8. Instantiate BlockTree
    /// 9. Launch read-only state provider
    /// 10. Create transaction comparer provider and transaction pool
    /// 11. Start receipt storage
    /// 12. Initialize virtual machine
    /// 13. Initialize transaction processor and block preprocessor step
    /// 14. Initialize header and block validators
    /// 15. Initialize block processor
    /// 16. Initialize and start chain processor
    /// 17. Create and initialize temp database provider for RPC calls
    /// 18. Initialize filter store and manager
    /// 19. Set up read-only processing environment
    /// 20. Initialize Bloom storage and log finder
    /// 21. Set up timestamper, blockchain bridge, and gas price oracle
    /// 22. Configure fee history oracle and sync config
    /// 23. Initialize nonce manager, wallet, and transaction signer
    /// 24. Create transaction sealer and sender
    /// 25. Set up EthRpcModule
    /// </summary>
    /// <param name="stateDb">Current state database</param>
    /// <param name="blockDb">Current Block database</param>
    /// <param name="headerDb">Current Header database</param>
    /// <param name="blockInfoDb">Current Block information database</param>
    /// <param name="codeDb">Current Code database</param>
    /// <param name="specProvider">Specification provider (optional)</param>
    /// <returns>MultiCallBlockchainFork instance</returns>
    public MultiCallBlockchainFork(IDbProvider oldDbProvider, ISpecProvider specProvider)
    {
        //Init BlockChain
        //Writable inMemory DBs on with access to the data of existing ones (will not modify existing data)
        if (oldDbProvider == null)
        {
            throw new ArgumentNullException();
        }

        var oldStack = oldDbProvider.StateDb.GetAllValues().ToList();
        IDb inMemStateDb = new ReadOnlyDb(oldDbProvider.StateDb, true);
        IDb inMemBlockDb = new ReadOnlyDb(oldDbProvider.BlocksDb, true);
        IDb inMemHeaderDb = new ReadOnlyDb(oldDbProvider.HeadersDb, true);
        IDb inMemBlockInfoDb = new ReadOnlyDb(oldDbProvider.BlockInfosDb, true);
        IDb inMemCodeDb = new ReadOnlyDb(oldDbProvider.CodeDb, true);
        IDb inMemMetadataDb = new ReadOnlyDb(oldDbProvider.MetadataDb, true);

        DbProvider dbProvider = new(DbModeHint.Mem);
        DbProvider = dbProvider;

        dbProvider.RegisterDb(DbNames.State, inMemStateDb);
        dbProvider.RegisterDb(DbNames.Blocks, inMemBlockDb);
        dbProvider.RegisterDb(DbNames.Headers, inMemHeaderDb);
        dbProvider.RegisterDb(DbNames.BlockInfos, inMemBlockInfoDb);
        dbProvider.RegisterDb(DbNames.Code, inMemCodeDb);
        dbProvider.RegisterDb(DbNames.Metadata, inMemMetadataDb);

        SpecProvider = specProvider;

        ILogManager logManager = SimpleConsoleLogManager.Instance;

        var newStack = inMemStateDb.GetAllValues().ToList();
        TrieStore trieStore = new(inMemStateDb, logManager);
        StateProvider = new(trieStore, inMemCodeDb, logManager);
        StorageProvider = new(trieStore, StateProvider, logManager);

        IReadOnlyTrieStore readOnlyTrieStore = trieStore.AsReadOnly(inMemStateDb);
        StateReader stateReader = new(readOnlyTrieStore, inMemCodeDb, logManager);
        SyncConfig syncConfig = new();
        BlockTree = new BlockTree(inMemBlockDb,
            inMemHeaderDb,
            inMemBlockInfoDb,
            inMemMetadataDb,
            new ChainLevelInfoRepository(inMemBlockInfoDb),
            SpecProvider,
            NullBloomStorage.Instance,
            syncConfig,
            LimboLogs.Instance);

        ChainHeadReadOnlyStateProvider readOnlyState = new(BlockTree, stateReader);

        TransactionComparerProvider transactionComparerProvider = new(SpecProvider, BlockTree);

        EthereumEcdsa ethereumEcdsa = new(SpecProvider.ChainId, logManager);

        Nethermind.TxPool.TxPool txPool = new(
            ethereumEcdsa,
            new ChainHeadInfoProvider(
                SpecProvider,
                BlockTree,
                readOnlyState),
            new TxPoolConfig(),
            new TxValidator(SpecProvider.ChainId),
            logManager,
            transactionComparerProvider.GetDefaultComparer());

        InMemoryReceiptStorage receiptStorage = new();

        VirtualMachine virtualMachine = new(
            new BlockhashProvider(BlockTree, logManager),
            SpecProvider,
            logManager);

        TransactionProcessor txProcessor = new(SpecProvider, StateProvider, StorageProvider, virtualMachine, logManager);
        RecoverSignatures blockPreprocessorStep = new(ethereumEcdsa, txPool, SpecProvider, logManager);

        HeaderValidator headerValidator = new(
            BlockTree,
            Always.Valid,
            SpecProvider,
            logManager);

        BlockValidator blockValidator = new(new TxValidator(SpecProvider.ChainId),
            headerValidator,
            Always.Valid,
            SpecProvider,
            logManager);

        BlockProcessor blockProcessor = new(SpecProvider,
            blockValidator,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, StateProvider),
            StateProvider,
            StorageProvider,
            receiptStorage,
            NullWitnessCollector.Instance,
            logManager);

        ChainProcessor = new(
            BlockTree,
            blockProcessor,
            blockPreprocessorStep,
            stateReader,
            logManager,
            BlockchainProcessor.Options.Default);
        ChainProcessor.Start();

        //Init RPC
        IFilterStore filterStore = new FilterStore();
        IFilterManager filterManager =
            new FilterManager(filterStore, blockProcessor, txPool, LimboLogs.Instance);
        ReadOnlyTxProcessingEnv processingEnv = new(
            new ReadOnlyDbProvider(DbProvider, false),
            new TrieStore(DbProvider.StateDb, LimboLogs.Instance).AsReadOnly(),
            new ReadOnlyBlockTree(BlockTree),
            SpecProvider,
            LimboLogs.Instance);

        BloomStorage bloomStorage =
            new(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
        ReceiptsRecovery receiptsRecovery =
            new(new EthereumEcdsa(SpecProvider.ChainId, LimboLogs.Instance), SpecProvider);
        IReceiptFinder receiptFinder = receiptStorage;
        LogFinder logFinder = new(BlockTree, receiptStorage, receiptStorage, bloomStorage,
            LimboLogs.Instance, receiptsRecovery);
        ITimestamper timestamper = Timestamper.Default;


        BlockchainBridge bridge = new(processingEnv,
            txPool,
            receiptFinder,
            filterStore,
            filterManager,
            ethereumEcdsa,
            timestamper,
            logFinder,
            SpecProvider,
            new BlocksConfig(),
            false);

        GasPriceOracle gasPriceOracle = new(BlockFinder, SpecProvider, logManager);
        FeeHistoryOracle feeHistoryOracle =
            new(BlockFinder, receiptStorage, SpecProvider);
        IChainHeadInfoProvider chainHeadInfoProvider =
            new ChainHeadInfoProvider(SpecProvider, BlockTree, stateReader);
        NonceManager nonceManager = new(chainHeadInfoProvider.AccountStateProvider);
        NullWallet? wallet = NullWallet.Instance; // May be use one from API directly?
        ITxSigner txSigner = new WalletTxSigner(wallet, SpecProvider.ChainId);
        TxSealer txSealer = new(txSigner, timestamper);
        TxPoolSender txSender = new(txPool, txSealer, nonceManager, ethereumEcdsa);

        EthRpcModule = new EthRpcModule(
            new JsonRpcConfig(),
            bridge,
            BlockFinder,
            stateReader,
            txPool,
            txSender,
            wallet,
            receiptFinder,
            LimboLogs.Instance,
            SpecProvider,
            gasPriceOracle,
            new EthSyncingInfo(BlockTree, receiptStorage, syncConfig, logManager),
            feeHistoryOracle);

    }

    public IDbProvider DbProvider { get; set; }

    //Create a new block next to current LatestBlock (BlockFinder.Head)
    public Block CreateBlock()
    {
        var parent = LatestBlock?.Header;
        if (parent == null)
            throw new Exception("Existing header expected");
        Keccak? headerHash = parent.Hash;


        BlockHeader blockHeader = new(
            parent.Hash,
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            parent.Difficulty, parent.Number + 1,
            parent.GasLimit,
            parent.Timestamp + 1,
            Array.Empty<byte>());


        return new Block(blockHeader);
    }

    //Process Block and UpdateMainChain with it
    public bool FinalizeBlock(Block CurrentBlock)
    {
        StorageProvider.Commit();
        StorageProvider.CommitTrees(CurrentBlock.Number);
        StateProvider.CommitTree(CurrentBlock.Number);
        CurrentBlock.Header.StateRoot = StateProvider.StateRoot;
        CurrentBlock.Header.Hash = CurrentBlock.Header.CalculateHash();

        AddBlockResult res = BlockTree.SuggestBlock(CurrentBlock, BlockTreeSuggestOptions.ForceSetAsMain);
        if (res != AddBlockResult.Added) return false;

        Block? current = ChainProcessor.Process(CurrentBlock!, ProcessingOptions.None, NullBlockTracer.Instance); //TODo: Maybe Trace!!
        if (current == null) return false;

        BlockTree.UpdateMainChain(new[] { current }, true);
        return true;
    }

    /// <summary>
    /// Forges a new block in the temp blockchain using the provided action to manipulate state, release spec, spec provider, and storage provider.
    /// </summary>
    /// <param name="action">An action representing the modifications to the blockchain state and storage.</param>
    public bool ForgeChainBlock(Action<IStateProvider ,
        IReleaseSpec , ISpecProvider , IStorageProvider > action)
    {
        //Prepare a block
        var newBlock = CreateBlock();
        var tt = StateProvider.StateRoot;
        var ttt = newBlock.Header.StateRoot;
        //newBlock.Header.s
        //Actual stuff
        action(StateProvider,
            CurrentSpec,
            SpecProvider,
            StorageProvider);

        //Add block
        bool results = FinalizeBlock(newBlock);
        return results;
    }

    //For using scope guard
    #region IDisposable

    private bool _disposed;
    public void Dispose()
    {
        Dispose(true);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            DbProvider?.Dispose();
        }
    }

    ~MultiCallBlockchainFork()
    {
        Dispose(false);
    }
    #endregion
}
