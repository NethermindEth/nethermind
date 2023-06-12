// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
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

/// <summary>
///     The MultiCallBlockchainFork class enables the creation of temporary in-memory blockchain instances,
///     based on a base chain's state. It's useful for simulating transactions, testing,
///     and executing smart contracts without affecting the base chain. The class offers RPC,
///     StateProvider, and StorageProvider utilities for swift chain data manipulation
///     and supports multi-block chain simulations using CreateBlock/FinaliseBlock pair manually or ForgeChainBlock method.
/// </summary>
public class MultiCallBlockchainFork : IDisposable
{
    private UInt256 _maxGas;

    //todo remove public useless bridge
    public BlockchainBridge bridge;

    public InMemoryReceiptStorage receiptStorage;


    //TODO: throw away and replace with ...Env related stuff based implementations (decompose, remove unused functions)
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
    public MultiCallBlockchainFork(IDbProvider oldDbProvider, ISpecProvider specProvider, UInt256 MaxGas)
    {
        _maxGas = MaxGas;
        BlockTracer = new MultiCallBlockTracer();
        //Init BlockChain
        //Writable inMemory DBs on with access to the data of existing ones (will not modify existing data)
        if (oldDbProvider == null) throw new ArgumentNullException();

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
            logManager);

        StateProvider.StateRoot = BlockTree.Head.StateRoot;

        ChainHeadReadOnlyStateProvider readOnlyState = new(BlockTree, stateReader);

        TransactionComparerProvider transactionComparerProvider = new(SpecProvider, BlockTree);

        EthereumEcdsa = new EthereumEcdsa(SpecProvider.ChainId, logManager);

        Nethermind.TxPool.TxPool txPool = new(
            EthereumEcdsa,
            new ChainHeadInfoProvider(
                SpecProvider,
                BlockTree,
                readOnlyState),
            new TxPoolConfig(),
            new TxValidator(SpecProvider.ChainId),
            logManager,
            transactionComparerProvider.GetDefaultComparer());


        receiptStorage = new InMemoryReceiptStorage();

        VirtualMachine = new MultiCallVirtualMachine(
            new BlockhashProvider(BlockTree, logManager),
            SpecProvider,
            logManager);

        TransactionProcessor txProcessor =
            new(SpecProvider, StateProvider, VirtualMachine, logManager);
        RecoverSignatures blockPreprocessorStep = new(EthereumEcdsa, txPool, SpecProvider, logManager);

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
            new FilterManager(filterStore, BlockProcessor, txPool, logManager);

        //ToDO make use of our VM!!!
        MultiCallReadOnlyTxProcessingEnv processingEnv = new(VirtualMachine,
            new ReadOnlyDbProvider(DbProvider, false),
            new TrieStore(DbProvider.StateDb, logManager).AsReadOnly(),
            new ReadOnlyBlockTree(BlockTree),
            SpecProvider,
            logManager);

        BloomStorage bloomStorage =
            new(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
        ReceiptsRecovery receiptsRecovery =
            new(new EthereumEcdsa(SpecProvider.ChainId, logManager), SpecProvider);
        IReceiptFinder receiptFinder = receiptStorage;
        LogFinder logFinder = new(BlockTree, receiptStorage, receiptStorage, bloomStorage,
            logManager, receiptsRecovery);
        ITimestamper timestamper = Timestamper.Default;


        bridge = new BlockchainBridge(processingEnv,
            txPool,
            receiptFinder,
            filterStore,
            filterManager,
            EthereumEcdsa,
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
        TxPoolSender txSender = new(txPool, txSealer, nonceManager, EthereumEcdsa);

        JsonRpcConfig? jsonRpcConfig = new();

        EthRpcModule = new EthRpcModule(
            jsonRpcConfig,
            bridge,
            BlockFinder,
            stateReader,
            txPool,
            txSender,
            wallet,
            receiptFinder,
            logManager,
            SpecProvider,
            gasPriceOracle,
            new EthSyncingInfo(BlockTree, receiptStorage, syncConfig, new StaticSelector(SyncMode.All), logManager),
            feeHistoryOracle,
            dbProvider);
    }

    public MultiCallBlockTracer BlockTracer { get; }

    private BlockchainProcessor ChainProcessor { get; }
    private BlockProcessor BlockProcessor { get; }

    public IWorldState StateProvider { get; internal set; }
    public ISpecProvider SpecProvider { get; internal set; }
    public IBlockTree BlockTree { get; internal set; }
    public IBlockFinder BlockFinder => BlockTree;
    public Block? LatestBlock => BlockFinder.Head;

    public EthRpcModule EthRpcModule { get; internal set; }

    public EthereumEcdsa EthereumEcdsa { get; internal set; }
    public IReleaseSpec CurrentSpec => SpecProvider.GetSpec(LatestBlock!.Header);
    public virtual MultiCallVirtualMachine VirtualMachine { get; internal set; }

    public IDbProvider DbProvider { get; internal set; }

    //Create a new block next to current LatestBlock (BlockFinder.Head)
    public Block CreateBlock(BlockOverride? desiredBlock, CallTransactionModel[] desiredTransactions)
    {
        BlockHeader? parent = null;
        BlockHeader blockHeader = null;

        if (desiredBlock == null)
        {
            parent = LatestBlock?.Header;

            blockHeader = new BlockHeader(
                parent.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                UInt256.Zero,
                parent.Number + 1,
                parent.GasLimit,
                parent.Timestamp + 1,
                Array.Empty<byte>());
            blockHeader.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, SpecProvider.GetSpec(blockHeader));
        }
        else
        {
            if (desiredBlock.Number < LatestBlock.Number)
            {
                Block? parentBlock = BlockFinder.FindBlock((long)(desiredBlock.Number - 1));
                if (parentBlock != null)
                {
                    BlockTree.UpdateMainChain(new List<Block> { parentBlock }, true, true);
                    BlockTree.UpdateHeadBlock(parentBlock.Hash);
                    parent = parentBlock.Header;
                    StateProvider.StateRoot = parent.StateRoot;
                }
                else
                    throw new ArgumentException("could not find parent block and load the state");
            }
            else
                parent = LatestBlock?.Header;


            ulong time = 0;
            if (desiredBlock.Time <= ulong.MaxValue)
                time = (ulong)desiredBlock.Time;
            else
                throw new OverflowException("Time value is too large to be converted to ulong we use.");

            long gasLimit = 0;
            if (desiredBlock.GasLimit <= long.MaxValue)
                gasLimit = (long)desiredBlock.GasLimit;
            else
                throw new OverflowException("GasLimit value is too large to be converted to long we use.");

            long blockNumber = 0;
            if (desiredBlock.Number <= long.MaxValue)
                blockNumber = (long)desiredBlock.Number;
            else
                throw new OverflowException("Block Number value is too large to be converted to long we use.");

            blockHeader = new BlockHeader(
                parent.Hash,
                Keccak.OfAnEmptySequenceRlp,
                desiredBlock.FeeRecipient,
                UInt256.Zero,
                blockNumber,
                gasLimit,
                time,
                Array.Empty<byte>());
            blockHeader.MixHash = desiredBlock.PrevRandao;
            blockHeader.BaseFeePerGas = desiredBlock.BaseFee;
        }

        if (_maxGas <= long.MaxValue)
        {
            blockHeader.GasLimit =
                (long)Math.Min((ulong)blockHeader.GasLimit, _maxGas.ToUInt64(CultureInfo.InvariantCulture));
        }
        else
            _maxGas -= new UInt256((ulong)blockHeader.GasLimit);

        UInt256 difficulty = ConstantDifficulty.One.Calculate(blockHeader, parent);
        blockHeader.Difficulty = difficulty;
        blockHeader.TotalDifficulty = parent.TotalDifficulty + difficulty;

        List<Transaction> transactions = new();
        if (desiredTransactions != null)
            transactions = desiredTransactions.Select(model => model.GetTransaction()).ToList();

        Block? block = new(blockHeader, transactions, Array.Empty<BlockHeader>());

        return block;
    }

    //Process Block and UpdateMainChain with it
    public Block FinalizeBlock(Block currentBlock)
    {
        StateProvider.Commit(SpecProvider.GetSpec(currentBlock.Header));
        StateProvider.CommitTree(currentBlock.Number);
        StateProvider.RecalculateStateRoot();

        currentBlock.Header.StateRoot = StateProvider.StateRoot;
        currentBlock.Header.IsPostMerge = true; //ToDo: Seal if necessary before merge 192 BPB
        currentBlock.Header.Hash = currentBlock.Header.CalculateHash();


        Block[]? blocks = BlockProcessor.Process(StateProvider.StateRoot,
            new List<Block> { currentBlock },
            ProcessingOptions.ForceProcessing |
            ProcessingOptions.NoValidation |
            ProcessingOptions.DoNotVerifyNonce |
            ProcessingOptions.IgnoreParentNotOnMainChain |
            ProcessingOptions.MarkAsProcessed |
            ProcessingOptions.StoreReceipts
            ,
            BlockTracer);
        Block? block = blocks.First();
        AddBlockResult res = BlockTree.SuggestBlock(block,
            BlockTreeSuggestOptions.ForceSetAsMain | BlockTreeSuggestOptions.ForceDontValidateParent);

        BlockTree.UpdateMainChain(blocks, true, true);
        BlockTree.UpdateHeadBlock(block.Hash);
        return block;
    }


    //Apply changes to accounts and contracts states including precompiles
    private void ModifyAccounts(AccountOverride[] StateOverrides)
    {
        Account? acc;
        if (StateOverrides == null) return;

        foreach (AccountOverride accountOverride in StateOverrides)
        {
            Address address = accountOverride.Address;
            bool accExists = StateProvider.AccountExists(address);
            if (!accExists)
            {
                StateProvider.CreateAccount(address, accountOverride.Balance, accountOverride.Nonce);
                acc = StateProvider.GetAccount(address);
            }
            else
                acc = StateProvider.GetAccount(address);

            UInt256 accBalance = acc.Balance;
            if (accBalance > accountOverride.Balance)
                StateProvider.SubtractFromBalance(address, accBalance - accountOverride.Balance, CurrentSpec);
            else if (accBalance < accountOverride.Nonce)
                StateProvider.AddToBalance(address, accountOverride.Balance - accBalance, CurrentSpec);

            UInt256 accNonce = acc.Nonce;
            if (accNonce > accountOverride.Nonce)
                StateProvider.DecrementNonce(address);
            else if (accNonce < accountOverride.Nonce) StateProvider.IncrementNonce(address);

            if (acc != null)
            {
                if (accountOverride.Code is not null)
                {
                    VirtualMachine.SetOverwrite(StateProvider, CurrentSpec, address,
                        new CodeInfo(accountOverride.Code), accountOverride.MoveToAddress);
                }
            }


            if (accountOverride.State is not null)
            {
                accountOverride.State = new Dictionary<UInt256, byte[]>();
                foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.State)
                    StateProvider.Set(new StorageCell(address, storage.Key),
                        storage.Value.WithoutLeadingZeros().ToArray());
            }

            if (accountOverride.StateDiff is not null)
            {
                foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.StateDiff)
                    StateProvider.Set(new StorageCell(address, storage.Key),
                        storage.Value.WithoutLeadingZeros().ToArray());
            }

            StateProvider.Commit(CurrentSpec);
        }
    }

    /// <summary>
    ///     Forges a new block in the temp blockchain using the provided action to manipulate state, release spec, spec
    ///     provider, and storage provider.
    /// </summary>
    /// <param name="forceStateAction">An action representing the modifications to the blockchain state and storage.</param>
    public Block ForgeChainBlock(MultiCallBlockStateCallsModel blockMock)
    {
        //Create a block
        Block? newBlock = CreateBlock(blockMock.BlockOverride, blockMock.Calls);

        //Update the state
        ModifyAccounts(blockMock.StateOverrides);

        //Process transactions
        newBlock = FinalizeBlock(newBlock);
        return newBlock;
    }

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
