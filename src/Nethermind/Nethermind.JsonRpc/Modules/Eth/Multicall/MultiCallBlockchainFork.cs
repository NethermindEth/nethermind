// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Eth.Multicall;

public class SimplifiedBlockValidator : BlockValidator
{
    public override bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
    {
        if (processedBlock.Header.StateRoot != suggestedBlock.Header.StateRoot)
        {
            //Note we mutate suggested block here to allow eth_multicall enforced State Root change
            //and still pass validation 
            suggestedBlock.Header.StateRoot = processedBlock.Header.StateRoot;
            suggestedBlock.Header.Hash = suggestedBlock.Header.CalculateHash();
        }

        bool isValid = processedBlock.Header.Hash == suggestedBlock.Header.Hash;

        if (processedBlock.Header.GasUsed != suggestedBlock.Header.GasUsed) isValid = false;

        if (processedBlock.Header.Bloom != suggestedBlock.Header.Bloom) isValid = false;

        if (processedBlock.Header.ReceiptsRoot != suggestedBlock.Header.ReceiptsRoot) isValid = false;


        for (int i = 0; i < processedBlock.Transactions.Length; i++)
            if (receipts[i].Error is not null && receipts[i].GasUsed == 0 && receipts[i].Error == "invalid")
                isValid = false;

        return isValid;
    }

    public SimplifiedBlockValidator(ITxValidator? txValidator,
        IHeaderValidator? headerValidator,
        IUnclesValidator? unclesValidator,
        ISpecProvider? specProvider,
        ILogManager? logManager) : base(txValidator, headerValidator, unclesValidator, specProvider, logManager)
    {
    }
}

/// <summary>
///     The MultiCallBlockchainFork class enables the creation of temporary in-memory blockchain instances,
///     based on a base chain's state. It's useful for simulating transactions, testing,
///     and executing smart contracts without affecting the base chain. The class offers RPC,
///     StateProvider, and StorageProvider utilities for swift chain data manipulation
///     and supports multi-block chain simulations using CreateBlock/FinaliseBlock pair manually or ForgeChainBlock method.
/// </summary>
public class MultiCallBlockchainFork : IDisposable
{
    private BlockchainProcessor ChainProcessor { get; }
    private BlockProcessor BlockProcessor { get; }

    public IWorldState StateProvider { get; internal set; }
    public ISpecProvider SpecProvider { get; internal set; }
    public IBlockTree BlockTree { get; internal set; }
    public IBlockFinder BlockFinder => BlockTree;
    public Block? LatestBlock => BlockFinder.Head;

    public EthRpcModule EthRpcModule { get; internal set; }
    public IReleaseSpec CurrentSpec => SpecProvider.GetSpec(LatestBlock!.Header);

    /// <summary>
    ///     Creates a MultiCallBlockchainFork instance with the following steps:
    ///     1. Initialize MultiCallBlockchainFork object
    ///     2. Create read-only in-memory databases
    ///     3. Set spec provider
    ///     4. Set up log manager
    ///     5. Establish EthereumEcdsa
    ///     6. Configure TrieStore, StateProvider, and StorageProvider
    ///     7. Prepare state reader
    ///     8. Instantiate BlockTree
    ///     9. Launch read-only state provider
    ///     10. Create transaction comparer provider and transaction pool
    ///     11. Start receipt storage
    ///     12. Initialize virtual machine
    ///     13. Initialize transaction processor and block preprocessor step
    ///     14. Initialize header and block validators
    ///     15. Initialize block processor
    ///     16. Initialize and start chain processor
    ///     17. Create and initialize temp database provider for RPC calls
    ///     18. Initialize filter store and manager
    ///     19. Set up read-only processing environment
    ///     20. Initialize Bloom storage and log finder
    ///     21. Set up timestamper, blockchain bridge, and gas price oracle
    ///     22. Configure fee history oracle and sync config
    ///     23. Initialize nonce manager, wallet, and transaction signer
    ///     24. Create transaction sealer and sender
    ///     25. Set up EthRpcModule
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
        if (oldDbProvider == null) throw new ArgumentNullException();
        
        List<byte[]>? oldStack = oldDbProvider.StateDb.GetAllValues().ToList();
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

        TrieStore trieStore = new(inMemStateDb, logManager);
        StateProvider = new WorldState(trieStore, inMemCodeDb, logManager);

        IReadOnlyTrieStore readOnlyTrieStore = trieStore.AsReadOnly(inMemStateDb);
        StateReader stateReader = new(readOnlyTrieStore, inMemCodeDb, logManager);
        SyncConfig syncConfig = new();
        BlockTree = new BlockTree(DbProvider.BlocksDb,
            DbProvider.HeadersDb,
            DbProvider.BlockInfosDb,
            DbProvider.MetadataDb,
            new ChainLevelInfoRepository(DbProvider.BlockInfosDb),
            SpecProvider,
            NullBloomStorage.Instance,
            syncConfig,
            LimboLogs.Instance);

        StateProvider.StateRoot = BlockTree.Head.StateRoot;

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

        TransactionProcessor txProcessor =
            new(SpecProvider, StateProvider, virtualMachine, logManager);
        RecoverSignatures blockPreprocessorStep = new(ethereumEcdsa, txPool, SpecProvider, logManager);

        HeaderValidator headerValidator = new(
            BlockTree,
            Always.Valid,
            SpecProvider,
            logManager);

        SimplifiedBlockValidator blockValidator = new(new TxValidator(SpecProvider.ChainId),
            headerValidator,
            Always.Valid,
            SpecProvider,
            logManager);

        BlockProcessor = new BlockProcessor(SpecProvider,
            blockValidator,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, StateProvider),
            StateProvider,
            receiptStorage,
            NullWitnessCollector.Instance,
            logManager);

        ChainProcessor = new BlockchainProcessor(
            BlockTree,
            BlockProcessor,
            blockPreprocessorStep,
            stateReader,
            logManager,
            BlockchainProcessor.Options.Default);
        ChainProcessor.Start();

        //Init RPC
        IFilterStore filterStore = new FilterStore();
        IFilterManager filterManager =
            new FilterManager(filterStore, BlockProcessor, txPool, LimboLogs.Instance);
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
            new EthSyncingInfo(BlockTree, receiptStorage, syncConfig, new StaticSelector(SyncMode.All), logManager),
            feeHistoryOracle);
    }

    public IDbProvider DbProvider { get; internal set; }

    //Create a new block next to current LatestBlock (BlockFinder.Head)
    public Block CreateBlock()
    {
        BlockHeader? parent = LatestBlock?.Header;
        if (parent == null)
            throw new Exception("Existing header expected");

        BlockHeader blockHeader = new(
            parent.Hash,
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.Zero,
            parent.Number + 1,
            parent.GasLimit,
            parent.Timestamp + 1,
            Array.Empty<byte>());

        UInt256 difficulty = ConstantDifficulty.One.Calculate(blockHeader, parent);
        blockHeader.Difficulty = difficulty;
        blockHeader.TotalDifficulty = parent.TotalDifficulty + difficulty;
        blockHeader.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, SpecProvider.GetSpec(blockHeader));

        blockHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
        blockHeader.TxRoot = Keccak.EmptyTreeHash;
        blockHeader.Bloom = Bloom.Empty;

        Block? block = new Block(blockHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>());

        block = ChainProcessor.Process(block,
            ProcessingOptions.ProducingBlock,
            NullBlockTracer.Instance);

        return block;
    }

    //Process Block and UpdateMainChain with it
    public bool FinalizeBlock(Block currentBlock)
    {
        StateProvider.Commit(SpecProvider.GetSpec(currentBlock.Header));
        StateProvider.CommitTree(currentBlock.Number);
        StateProvider.RecalculateStateRoot();

        currentBlock.Header.StateRoot = StateProvider.StateRoot;
        currentBlock.Header.IsPostMerge = true; //ToDo: Seal if necessary before merge 192 BPB
        currentBlock.Header.Hash = currentBlock.Header.CalculateHash();

        AddBlockResult res = BlockTree.SuggestBlock(currentBlock, BlockTreeSuggestOptions.ForceSetAsMain);
        if (res != AddBlockResult.Added) return false;

        //ChainProcessor uses parent.StateRoot with the Process, it gets bad on InitBranch due to _state/storage resets
        Block[]? blocks = BlockProcessor.Process(currentBlock.StateRoot,
            new List<Block> { currentBlock! },
            ProcessingOptions.ForceProcessing,
            new BlockReceiptsTracer());

        BlockTree.UpdateMainChain(blocks, true, true);

        return true;
    }

    /// <summary>
    ///     Forges a new block in the temp blockchain using the provided action to manipulate state, release spec, spec
    ///     provider, and storage provider.
    /// </summary>
    /// <param name="action">An action representing the modifications to the blockchain state and storage.</param>
    public bool ForgeChainBlock(Action<IWorldState,
        IReleaseSpec, ISpecProvider> action)
    {
        //Prepare a block
        Block? newBlock = CreateBlock();

        action(StateProvider,
            CurrentSpec,
            SpecProvider
            );

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
