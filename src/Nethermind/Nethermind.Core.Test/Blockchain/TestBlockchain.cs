// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.BeaconBlockRoot;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Core.Test.Blockchain;

public class TestBlockchain : IDisposable
{
    public const int DefaultTimeout = 10000;
    public IStateReader StateReader { get; private set; } = null!;
    public IEthereumEcdsa EthereumEcdsa { get; private set; } = null!;
    public INonceManager NonceManager { get; private set; } = null!;
    public TransactionProcessor TxProcessor { get; set; } = null!;
    public IReceiptStorage ReceiptStorage { get; set; } = null!;
    public ITxPool TxPool { get; set; } = null!;
    public IDb CodeDb => DbProvider.CodeDb;
    public IBlockProcessor BlockProcessor { get; set; } = null!;
    public IBeaconBlockRootHandler BeaconBlockRootHandler { get; set; } = null!;
    public IBlockchainProcessor BlockchainProcessor { get; set; } = null!;

    public IBlockPreprocessorStep BlockPreprocessorStep { get; set; } = null!;

    public IBlockProcessingQueue BlockProcessingQueue { get; set; } = null!;
    public IBlockTree BlockTree { get; set; } = null!;

    public IBlockFinder BlockFinder
    {
        get => _blockFinder ?? BlockTree;
        set => _blockFinder = value;
    }

    public ILogFinder LogFinder { get; private set; } = null!;
    public IJsonSerializer JsonSerializer { get; set; } = null!;
    public IWorldState State { get; set; } = null!;
    public IReadOnlyStateProvider ReadOnlyState { get; private set; } = null!;
    public IDb StateDb => DbProvider.StateDb;
    public TrieStore TrieStore { get; set; } = null!;
    public IBlockProducer BlockProducer { get; private set; } = null!;
    public IDbProvider DbProvider { get; set; } = null!;
    public ISpecProvider SpecProvider { get; set; } = null!;

    public ISealEngine SealEngine { get; set; } = null!;

    public ITransactionComparerProvider TransactionComparerProvider { get; set; } = null!;

    public IPoSSwitcher PoSSwitcher { get; set; } = null!;

    protected TestBlockchain()
    {
    }

    public string SealEngineType { get; set; } = null!;

    public static Address AccountA = TestItem.AddressA;
    public static Address AccountB = TestItem.AddressB;
    public static Address AccountC = TestItem.AddressC;
    public SemaphoreSlim _resetEvent = null!;
    private ManualResetEvent _suggestedBlockResetEvent = null!;
    private AutoResetEvent _oneAtATime = new(true);
    private IBlockFinder _blockFinder = null!;

    public static readonly UInt256 InitialValue = 1000.Ether();
    private TrieStoreBoundaryWatcher _trieStoreWatcher = null!;
    public IHeaderValidator HeaderValidator { get; set; } = null!;

    public IBlockValidator BlockValidator { get; set; } = null!;
    public BuildBlocksWhenRequested BlockProductionTrigger { get; } = new();

    public IReadOnlyTrieStore ReadOnlyTrieStore { get; private set; } = null!;

    public ManualTimestamper Timestamper { get; protected set; } = null!;

    public ProducedBlockSuggester Suggester { get; protected set; } = null!;

    public static TransactionBuilder<Transaction> BuildSimpleTransaction => Builders.Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).To(AccountB);

    protected virtual async Task<TestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null, bool addBlockOnStart = true)
    {
        Timestamper = new ManualTimestamper(new DateTime(2020, 2, 15, 12, 50, 30, DateTimeKind.Utc));
        JsonSerializer = new EthereumJsonSerializer();
        SpecProvider = CreateSpecProvider(specProvider ?? MainnetSpecProvider.Instance);
        EthereumEcdsa = new EthereumEcdsa(SpecProvider.ChainId, LogManager);
        DbProvider = await CreateDbProvider();
        TrieStore = new TrieStore(StateDb, LogManager);
        State = new WorldState(TrieStore, DbProvider.CodeDb, LogManager);

        // Eip4788 precompile state account
        if (specProvider?.GenesisSpec?.IsBeaconBlockRootAvailable ?? false)
        {
            State.CreateAccount(SpecProvider.GenesisSpec.Eip4788ContractAddress, 1);
        }

        State.CreateAccount(TestItem.AddressA, (initialValues ?? InitialValue));
        State.CreateAccount(TestItem.AddressB, (initialValues ?? InitialValue));
        State.CreateAccount(TestItem.AddressC, (initialValues ?? InitialValue));

        byte[] code = Bytes.FromHexString("0xabcd");
        Keccak codeHash = Keccak.Compute(code);
        State.InsertCode(TestItem.AddressA, code, SpecProvider.GenesisSpec);

        State.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));

        State.Commit(SpecProvider.GenesisSpec);
        State.CommitTree(0);

        ReadOnlyTrieStore = TrieStore.AsReadOnly(StateDb);
        StateReader = new StateReader(ReadOnlyTrieStore, CodeDb, LogManager);

        IDb blockDb = new MemDb();
        IDb headerDb = new MemDb();
        IDb blockInfoDb = new MemDb();
        BlockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), SpecProvider, NullBloomStorage.Instance, LimboLogs.Instance);
        ReadOnlyState = new ChainHeadReadOnlyStateProvider(BlockTree, StateReader);
        TransactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockTree);
        TxPool = CreateTxPool();

        IChainHeadInfoProvider chainHeadInfoProvider =
            new ChainHeadInfoProvider(SpecProvider, BlockTree, StateReader);

        NonceManager = new NonceManager(chainHeadInfoProvider.AccountStateProvider);

        _trieStoreWatcher = new TrieStoreBoundaryWatcher(TrieStore, BlockTree, LogManager);

        ReceiptStorage = new InMemoryReceiptStorage();
        VirtualMachine virtualMachine = new(new BlockhashProvider(BlockTree, LogManager), SpecProvider, LogManager);
        TxProcessor = new TransactionProcessor(SpecProvider, State, virtualMachine, LogManager);
        BlockPreprocessorStep = new RecoverSignatures(EthereumEcdsa, TxPool, SpecProvider, LogManager);
        HeaderValidator = new HeaderValidator(BlockTree, Always.Valid, SpecProvider, LogManager);

        new ReceiptCanonicalityMonitor(BlockTree, ReceiptStorage, LogManager);

        BlockValidator = new BlockValidator(
            new TxValidator(SpecProvider.ChainId),
            HeaderValidator,
            Always.Valid,
            SpecProvider,
            LogManager);

        PoSSwitcher = NoPoS.Instance;
        ISealer sealer = new NethDevSealEngine(TestItem.AddressD);
        SealEngine = new SealEngine(sealer, Always.Valid);

        BloomStorage bloomStorage = new(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
        ReceiptsRecovery receiptsRecovery = new(new EthereumEcdsa(SpecProvider.ChainId, LimboLogs.Instance), SpecProvider);
        LogFinder = new LogFinder(BlockTree, ReceiptStorage, ReceiptStorage, bloomStorage, LimboLogs.Instance, receiptsRecovery);
        BeaconBlockRootHandler = new BeaconBlockRootHandler();
        BlockProcessor = CreateBlockProcessor();

        BlockchainProcessor chainProcessor = new(BlockTree, BlockProcessor, BlockPreprocessorStep, StateReader, LogManager, Consensus.Processing.BlockchainProcessor.Options.Default);
        BlockchainProcessor = chainProcessor;
        BlockProcessingQueue = chainProcessor;
        chainProcessor.Start();

        TxPoolTxSource txPoolTxSource = CreateTxPoolTxSource();
        ITransactionComparerProvider transactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockFinder);
        BlockProducer = CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
        await BlockProducer.Start();
        Suggester = new ProducedBlockSuggester(BlockTree, BlockProducer);

        _resetEvent = new SemaphoreSlim(0);
        _suggestedBlockResetEvent = new ManualResetEvent(true);
        BlockTree.NewHeadBlock += OnNewHeadBlock;
        BlockProducer.BlockProduced += (s, e) =>
        {
            _suggestedBlockResetEvent.Set();
        };

        Block? genesis = GetGenesisBlock();
        BlockTree.SuggestBlock(genesis);

        await WaitAsync(_resetEvent, "Failed to process genesis in time.");

        if (addBlockOnStart)
            await AddBlocksOnStart();

        return this;
    }

    private static ISpecProvider CreateSpecProvider(ISpecProvider specProvider)
    {
        return specProvider is TestSpecProvider { AllowTestChainOverride: false }
            ? specProvider
            : new OverridableSpecProvider(specProvider, s => new OverridableReleaseSpec(s) { IsEip3607Enabled = false });
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        _resetEvent.Release(1);
    }

    protected virtual Task<IDbProvider> CreateDbProvider() => TestMemDbProvider.InitAsync();

    private async Task WaitAsync(SemaphoreSlim semaphore, string error, int timeout = DefaultTimeout)
    {
        if (!await semaphore.WaitAsync(timeout))
        {
            throw new InvalidOperationException(error);
        }
    }

    private async Task WaitAsync(EventWaitHandle eventWaitHandle, string error, int timeout = DefaultTimeout)
    {
        if (!await eventWaitHandle.WaitOneAsync(timeout, CancellationToken.None))
        {
            throw new InvalidOperationException(error);
        }
    }

    protected virtual IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
    {
        BlocksConfig blocksConfig = new();

        BlockProducerEnvFactory blockProducerEnvFactory = new(
            DbProvider,
            BlockTree,
            ReadOnlyTrieStore,
            SpecProvider,
            BlockValidator,
            NoBlockRewards.Instance,
            ReceiptStorage,
            BlockPreprocessorStep,
            TxPool,
            transactionComparerProvider,
            blocksConfig,
            LogManager);

        BlockProducerEnv env = blockProducerEnvFactory.Create(txPoolTxSource);
        return new TestBlockProducer(
            env.TxSource,
            env.ChainProcessor,
            env.ReadOnlyStateProvider,
            sealer,
            BlockTree,
            BlockProductionTrigger,
            Timestamper,
            SpecProvider,
            LogManager,
            blocksConfig);
    }

    public virtual ILogManager LogManager { get; set; } = LimboLogs.Instance;

    protected virtual TxPool.TxPool CreateTxPool() =>
        new(
            EthereumEcdsa,
            new ChainHeadInfoProvider(new FixedForkActivationChainHeadSpecProvider(SpecProvider), BlockTree, ReadOnlyState),
            new TxPoolConfig(),
            new TxValidator(SpecProvider.ChainId),
            LogManager,
            TransactionComparerProvider.GetDefaultComparer());

    protected virtual TxPoolTxSource CreateTxPoolTxSource()
    {
        BlocksConfig blocksConfig = new()
        {
            MinGasPrice = 0
        };
        ITxFilterPipeline txFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(LimboLogs.Instance,
            SpecProvider, blocksConfig);
        return new TxPoolTxSource(TxPool, SpecProvider, TransactionComparerProvider, LogManager, txFilterPipeline);
    }

    public BlockBuilder GenesisBlockBuilder { get; set; } = null!;

    protected virtual Block GetGenesisBlock()
    {
        BlockBuilder genesisBlockBuilder = Builders.Build.A.Block.Genesis;
        if (GenesisBlockBuilder is not null)
        {
            genesisBlockBuilder = GenesisBlockBuilder;
        }

        if (SealEngineType == Core.SealEngineType.AuRa)
        {
            genesisBlockBuilder.WithAura(0, new byte[65]);
        }

        if (SpecProvider.GenesisSpec.IsEip4844Enabled)
        {
            genesisBlockBuilder.WithBlobGasUsed(0);
            genesisBlockBuilder.WithExcessBlobGas(0);
        }


        if (SpecProvider.GenesisSpec.IsBeaconBlockRootAvailable)
        {
            genesisBlockBuilder.WithParentBeaconBlockRoot(Keccak.Zero);
        }

        genesisBlockBuilder.WithStateRoot(State.StateRoot);
        return genesisBlockBuilder.TestObject;
    }

    protected virtual async Task AddBlocksOnStart()
    {
        await AddBlock();
        await AddBlock(BuildSimpleTransaction.WithNonce(0).TestObject);
        await AddBlock(BuildSimpleTransaction.WithNonce(1).TestObject, BuildSimpleTransaction.WithNonce(2).TestObject);
    }

    protected virtual IBlockProcessor CreateBlockProcessor() =>
        new BlockProcessor(
            SpecProvider,
            BlockValidator,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
            State,
            ReceiptStorage,
            NullWitnessCollector.Instance,
            LogManager);

    public async Task WaitForNewHead()
    {
        await WaitAsync(_resetEvent, "Failed to produce new head in time.");
        _suggestedBlockResetEvent.Reset();
    }

    public async Task AddBlock(params Transaction[] transactions)
    {
        await AddBlockInternal(transactions);

        await WaitAsync(_resetEvent, "Failed to produce new head in time.");
        _suggestedBlockResetEvent.Reset();
        _oneAtATime.Set();
    }

    public async Task AddBlock(bool shouldWaitForHead = true, params Transaction[] transactions)
    {
        await AddBlockInternal(transactions);

        if (shouldWaitForHead)
        {
            await WaitAsync(_resetEvent, "Failed to produce new head in time.");
        }
        else
        {
            await WaitAsync(_suggestedBlockResetEvent, "Failed to produce new suggested block in time.");
        }

        _oneAtATime.Set();
    }

    private async Task<AcceptTxResult[]> AddBlockInternal(params Transaction[] transactions)
    {
        // we want it to be last event, so lets re-register
        BlockTree.NewHeadBlock -= OnNewHeadBlock;
        BlockTree.NewHeadBlock += OnNewHeadBlock;

        await WaitAsync(_oneAtATime, "Multiple block produced at once.");
        AcceptTxResult[] txResults = transactions.Select(t => TxPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
        Timestamper.Add(TimeSpan.FromSeconds(1));
        await BlockProductionTrigger.BuildBlock();
        return txResults;
    }

    public void AddTransactions(params Transaction[] txs)
    {
        for (int i = 0; i < txs.Length; i++)
        {
            TxPool.SubmitTx(txs[i], TxHandlingOptions.None);
        }
    }

    public virtual void Dispose()
    {
        BlockProducer?.StopAsync();
        if (DbProvider != null)
        {
            CodeDb?.Dispose();
            StateDb?.Dispose();
        }

        _trieStoreWatcher?.Dispose();
        DbProvider?.Dispose();
    }

    /// <summary>
    /// Creates a simple transfer transaction with value defined by <paramref name="ether"/>
    /// from a rich account to <paramref name="address"/>
    /// </summary>
    /// <param name="address">Address to add funds to</param>
    /// <param name="ether">Value of ether to add to the account</param>
    /// <returns></returns>
    public async Task AddFunds(Address address, UInt256 ether) =>
        await AddBlock(GetFundsTransaction(address, ether));

    public async Task AddFunds(params (Address address, UInt256 ether)[] funds) =>
        await AddBlock(funds.Select((f, i) => GetFundsTransaction(f.address, f.ether, (uint)i)).ToArray());

    public async Task AddFundsAfterLondon(params (Address address, UInt256 ether)[] funds) =>
        await AddBlock(funds.Select((f, i) => GetFunds1559Transaction(f.address, f.ether, (uint)i)).ToArray());

    private Transaction GetFundsTransaction(Address address, UInt256 ether, uint index = 0)
    {
        UInt256 nonce = StateReader.GetNonce(BlockTree.Head!.StateRoot!, TestItem.AddressA);
        Transaction tx = Builders.Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyA)
            .To(address)
            .WithNonce(nonce + index)
            .WithValue(ether)
            .TestObject;
        return tx;
    }

    private Transaction GetFunds1559Transaction(Address address, UInt256 ether, uint index = 0)
    {
        UInt256 nonce = StateReader.GetNonce(BlockTree.Head!.StateRoot!, TestItem.AddressA);
        Transaction tx = Builders.Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyA)
            .To(address)
            .WithNonce(nonce + index)
            .WithMaxFeePerGas(20.GWei())
            .WithMaxPriorityFeePerGas(5.GWei())
            .WithType(TxType.EIP1559)
            .WithValue(ether)
            .WithChainId(MainnetSpecProvider.Instance.ChainId)
            .TestObject;
        return tx;
    }
}
