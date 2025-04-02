// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Utils;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Find;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Blockchain;

public class TestBlockchain : IDisposable
{
    public const int DefaultTimeout = 10000;
    public long TestTimout { get; set; } = DefaultTimeout;
    public IStateReader StateReader => Container.Resolve<IStateReader>();
    public IEthereumEcdsa EthereumEcdsa { get; private set; } = null!;
    public INonceManager NonceManager { get; private set; } = null!;
    public TransactionProcessor TxProcessor { get; set; } = null!;
    public IReceiptStorage ReceiptStorage { get; set; } = null!;
    public ITxPool TxPool { get; set; } = null!;
    public IWorldStateManager WorldStateManager => Container.Resolve<IWorldStateManager>();
    public IBlockProcessor BlockProcessor { get; set; } = null!;
    public IBlockchainProcessor BlockchainProcessor { get; set; } = null!;

    public IBlockPreprocessorStep BlockPreprocessorStep { get; set; } = null!;

    public IBlockProcessingQueue BlockProcessingQueue { get; set; } = null!;
    public IBlockTree BlockTree => Container.Resolve<IBlockTree>();

    public Action<IWorldState>? InitialStateMutator { get; set; }

    public IBlockFinder BlockFinder
    {
        get => _blockFinder ?? BlockTree;
        set => _blockFinder = value;
    }

    public ILogFinder LogFinder { get; private set; } = null!;
    public IJsonSerializer JsonSerializer { get; set; } = null!;
    public IReadOnlyStateProvider ReadOnlyState { get; private set; } = null!;
    public IDb StateDb => DbProvider.StateDb;
    public IDb BlocksDb => DbProvider.BlocksDb;
    public TrieStore TrieStore => Container.Resolve<TrieStore>();
    public IBlockProducer BlockProducer { get; private set; } = null!;
    public IBlockProducerRunner BlockProducerRunner { get; protected set; } = null!;
    public IDbProvider DbProvider => Container.Resolve<IDbProvider>();
    public ISpecProvider SpecProvider { get; set; } = null!;

    public ISealEngine SealEngine { get; set; } = null!;

    public ITransactionComparerProvider TransactionComparerProvider { get; set; } = null!;

    public IPoSSwitcher PoSSwitcher { get; set; } = null!;

    protected TestBlockchain()
    {
    }

    public string SealEngineType { get; set; } = null!;

    public static readonly Address AccountA = TestItem.AddressA;
    public static readonly Address AccountB = TestItem.AddressB;
    public static readonly Address AccountC = TestItem.AddressC;
    private IBlockFinder _blockFinder = null!;

    public static readonly DateTime InitialTimestamp = new(2020, 2, 15, 12, 50, 30, DateTimeKind.Utc);

    public static readonly UInt256 InitialValue = 1000.Ether();
    private TrieStoreBoundaryWatcher _trieStoreWatcher = null!;
    public IHeaderValidator HeaderValidator { get; set; } = null!;

    private ReceiptCanonicalityMonitor? _canonicalityMonitor;
    protected AutoCancelTokenSource _cts;
    public CancellationToken CancellationToken => _cts.Token;
    private TestBlockchainUtil _testUtil = null!;

    public IBlockValidator BlockValidator { get; set; } = null!;

    public IBeaconBlockRootHandler BeaconBlockRootHandler { get; set; } = null!;
    public BuildBlocksWhenRequested BlockProductionTrigger { get; } = new();

    public ManualTimestamper Timestamper { get; protected set; } = null!;
    public BlocksConfig BlocksConfig { get; protected set; } = new();

    public ProducedBlockSuggester Suggester { get; protected set; } = null!;

    public IExecutionRequestsProcessor? ExecutionRequestsProcessor { get; protected set; } = null!;
    public IChainLevelInfoRepository ChainLevelInfoRepository => Container.Resolve<IChainLevelInfoRepository>();

    public static TransactionBuilder<Transaction> BuildSimpleTransaction => Builders.Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).To(AccountB);

    protected IContainer Container { get; set; } = null!;

    public class Configuration
    {
        public bool SuggestGenesisOnStart = true;
    }

    protected virtual async Task<TestBlockchain> Build(
        ISpecProvider? specProvider = null,
        UInt256? initialValues = null,
        bool addBlockOnStart = true,
        long slotTime = 1,
        Action<ContainerBuilder>? configurer = null)
    {
        Timestamper = new ManualTimestamper(InitialTimestamp);
        JsonSerializer = new EthereumJsonSerializer();
        SpecProvider = CreateSpecProvider(specProvider ?? MainnetSpecProvider.Instance);
        EthereumEcdsa = new EthereumEcdsa(SpecProvider.ChainId);

        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider()))
            .AddSingleton<ISpecProvider>(SpecProvider)
            .AddSingleton<Configuration>()

            // Need to manually create the WorldStateManager to expose the triestore which is normally hidden
            // This means it does not use pruning triestore normally though.
            .AddSingleton<TrieStore>(ctx => new TrieStore(ctx.Resolve<IDbProvider>().StateDb, LimboLogs.Instance))
            .Bind<IPruningTrieStore, TrieStore>()
            .AddSingleton<IWorldStateManager>(ctx =>
            {
                IDbProvider dbProvider = ctx.Resolve<IDbProvider>();
                TrieStore trieStore = ctx.Resolve<TrieStore>();
                PreBlockCaches preBlockCaches = new PreBlockCaches();
                WorldState worldState = new WorldState(trieStore, dbProvider.CodeDb, LimboLogs.Instance, preBlockCaches: preBlockCaches);
                return new WorldStateManager(worldState, trieStore, dbProvider, LimboLogs.Instance);
            })

            ;

        await ConfigureContainer(builder);
        configurer?.Invoke(builder);

        Container = builder.Build();

        IWorldState state = Container.Resolve<IWorldStateManager>().GlobalWorldState;

        // Eip4788 precompile state account
        if (specProvider?.GenesisSpec?.IsBeaconBlockRootAvailable ?? false)
        {
            state.CreateAccount(SpecProvider.GenesisSpec.Eip4788ContractAddress!, 1);
        }

        // Eip2935
        if (specProvider?.GenesisSpec?.IsBlockHashInStateAvailable ?? false)
        {
            state.CreateAccount(SpecProvider.GenesisSpec.Eip2935ContractAddress, 1);
        }

        state.CreateAccount(TestItem.AddressA, initialValues ?? InitialValue);
        state.CreateAccount(TestItem.AddressB, initialValues ?? InitialValue);
        state.CreateAccount(TestItem.AddressC, initialValues ?? InitialValue);

        InitialStateMutator?.Invoke(state);

        byte[] code = Bytes.FromHexString("0xabcd");
        state.InsertCode(TestItem.AddressA, code, SpecProvider.GenesisSpec);

        state.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));

        state.Commit(SpecProvider.GenesisSpec);
        state.CommitTree(0);

        ReadOnlyState = new ChainHeadReadOnlyStateProvider(BlockTree, StateReader);
        TransactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockTree);
        CodeInfoRepository codeInfoRepository = new();
        TxPool = CreateTxPool(codeInfoRepository);

        IChainHeadInfoProvider chainHeadInfoProvider =
            new ChainHeadInfoProvider(SpecProvider, BlockTree, StateReader, codeInfoRepository);

        NonceManager = new NonceManager(chainHeadInfoProvider.ReadOnlyStateProvider);

        _trieStoreWatcher = new TrieStoreBoundaryWatcher(WorldStateManager, BlockTree, LogManager);

        ReceiptStorage = new InMemoryReceiptStorage(blockTree: BlockTree);
        VirtualMachine virtualMachine = new(new BlockhashProvider(BlockTree, SpecProvider, state, LogManager), SpecProvider, codeInfoRepository, LogManager);
        TxProcessor = new TransactionProcessor(SpecProvider, state, virtualMachine, codeInfoRepository, LogManager);

        BlockPreprocessorStep = new RecoverSignatures(EthereumEcdsa, TxPool, SpecProvider, LogManager);
        HeaderValidator = new HeaderValidator(BlockTree, Always.Valid, SpecProvider, LogManager);

        _canonicalityMonitor ??= new ReceiptCanonicalityMonitor(ReceiptStorage, LogManager);
        BeaconBlockRootHandler = new BeaconBlockRootHandler(TxProcessor, state);

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
        ReceiptsRecovery receiptsRecovery = new(new EthereumEcdsa(SpecProvider.ChainId), SpecProvider);
        LogFinder = new LogFinder(BlockTree, ReceiptStorage, ReceiptStorage, bloomStorage, LimboLogs.Instance, receiptsRecovery);
        BlockProcessor = CreateBlockProcessor(WorldStateManager.GlobalWorldState);

        BlockchainProcessor chainProcessor = new(BlockTree, BlockProcessor, BlockPreprocessorStep, StateReader, LogManager, Consensus.Processing.BlockchainProcessor.Options.Default);
        BlockchainProcessor = chainProcessor;
        BlockProcessingQueue = chainProcessor;
        chainProcessor.Start();

        TxPoolTxSource txPoolTxSource = CreateTxPoolTxSource();
        ITransactionComparerProvider transactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockFinder);
        BlockProducer = CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
        BlockProducerRunner ??= CreateBlockProducerRunner();
        BlockProducerRunner.Start();
        Suggester = new ProducedBlockSuggester(BlockTree, BlockProducerRunner);

        _cts = AutoCancelTokenSource.ThatCancelAfter(TimeSpan.FromMilliseconds(TestTimout));
        _testUtil = new TestBlockchainUtil(
            BlockProducerRunner,
            BlockProductionTrigger,
            Timestamper,
            BlockTree,
            TxPool,
            slotTime
        );

        Configuration testConfiguration = Container.Resolve<Configuration>();
        if (testConfiguration.SuggestGenesisOnStart)
        {
            Task waitGenesis = WaitForNewHead();
            Block? genesis = GetGenesisBlock(WorldStateManager.GlobalWorldState);
            BlockTree.SuggestBlock(genesis);
            await waitGenesis;
        }

        if (addBlockOnStart)
            await AddBlocksOnStart();

        return this;
    }

    protected virtual Task ConfigureContainer(ContainerBuilder builder)
    {
        return Task.CompletedTask;
    }

    private static ISpecProvider CreateSpecProvider(ISpecProvider specProvider)
    {
        return specProvider is TestSpecProvider { AllowTestChainOverride: false }
            ? specProvider
            : new OverridableSpecProvider(specProvider, static s => new OverridableReleaseSpec(s) { IsEip3607Enabled = false });
    }

    protected virtual IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
    {
        BlockProducerEnvFactory blockProducerEnvFactory = new(
            WorldStateManager,
            BlockTree,
            SpecProvider,
            BlockValidator,
            NoBlockRewards.Instance,
            ReceiptStorage,
            BlockPreprocessorStep,
            TxPool,
            transactionComparerProvider,
            BlocksConfig,
            LogManager);

        BlockProducerEnv env = blockProducerEnvFactory.Create(txPoolTxSource);
        return new TestBlockProducer(
            env.TxSource,
            env.ChainProcessor,
            env.ReadOnlyStateProvider,
            sealer,
            BlockTree,
            Timestamper,
            SpecProvider,
            LogManager,
            BlocksConfig);
    }

    protected virtual IBlockProducerRunner CreateBlockProducerRunner()
    {
        return new StandardBlockProducerRunner(BlockProductionTrigger, BlockTree, BlockProducer);
    }

    public virtual ILogManager LogManager { get; set; } = LimboLogs.Instance;

    protected virtual TxPool.TxPool CreateTxPool(CodeInfoRepository codeInfoRepository) =>
        new(
            EthereumEcdsa,
            new BlobTxStorage(),
            new ChainHeadInfoProvider(new FixedForkActivationChainHeadSpecProvider(SpecProvider), BlockTree, ReadOnlyState, codeInfoRepository) { HasSynced = true },
            new TxPoolConfig { BlobsSupport = BlobsSupportMode.InMemory },
            new TxValidator(SpecProvider.ChainId),
            LogManager,
            TransactionComparerProvider.GetDefaultComparer());

    protected virtual TxPoolTxSource CreateTxPoolTxSource()
    {
        BlocksConfig blocksConfig = new()
        {
            MinGasPrice = 0
        };
        ITxFilterPipeline txFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(LogManager, SpecProvider, blocksConfig);
        return new TxPoolTxSource(TxPool, SpecProvider, TransactionComparerProvider, LogManager, txFilterPipeline);
    }

    public BlockBuilder GenesisBlockBuilder { get; set; } = null!;

    protected virtual Block GetGenesisBlock(IWorldState state)
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

        if (SpecProvider.GenesisSpec.RequestsEnabled)
        {
            genesisBlockBuilder.WithEmptyRequestsHash();
        }

        genesisBlockBuilder.WithStateRoot(state.StateRoot);
        return genesisBlockBuilder.TestObject;
    }

    protected virtual async Task AddBlocksOnStart()
    {
        await AddBlock();
        await AddBlock(BuildSimpleTransaction.WithNonce(0).TestObject);
        await AddBlock(BuildSimpleTransaction.WithNonce(1).TestObject, BuildSimpleTransaction.WithNonce(2).TestObject);
    }

    protected virtual IBlockProcessor CreateBlockProcessor(IWorldState state) =>
        new BlockProcessor(
            SpecProvider,
            BlockValidator,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, state),
            state,
            ReceiptStorage,
            TxProcessor,
            new BeaconBlockRootHandler(TxProcessor, state),
            new BlockhashStore(SpecProvider, state),
            LogManager,
            preWarmer: CreateBlockCachePreWarmer(),
            executionRequestsProcessor: ExecutionRequestsProcessor);


    protected IBlockCachePreWarmer CreateBlockCachePreWarmer() =>
        new BlockCachePreWarmer(
            new ReadOnlyTxProcessingEnvFactory(
                WorldStateManager,
                BlockTree,
                SpecProvider,
                LogManager),
            WorldStateManager.GlobalWorldState,
            SpecProvider,
            4,
            LogManager,
            (WorldStateManager.GlobalWorldState as IPreBlockCaches)?.Caches);

    public async Task WaitForNewHead()
    {
        await BlockTree.WaitForNewBlock(_cts.Token);
    }

    public Task WaitForNewHeadWhere(Func<Block, bool> predicate)
    {
        return Wait.ForEventCondition<BlockReplacementEventArgs>(
            _cts.Token,
            (h) => BlockTree.BlockAddedToMain += h,
            (h) => BlockTree.BlockAddedToMain -= h,
            (e) => predicate(e.Block));
    }

    public async Task AddBlock(params Transaction[] transactions)
    {
        await _testUtil.AddBlockAndWaitForHead(_cts.Token, transactions);
    }

    public async Task AddBlockDoNotWaitForHead(params Transaction[] transactions)
    {
        await _testUtil.AddBlockDoNotWaitForHead(_cts.Token, transactions);
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
        BlockProducerRunner?.StopAsync();
        if (DbProvider is not null)
        {
            StateDb?.Dispose();
            DbProvider.BlobTransactionsDb?.Dispose();
            DbProvider.ReceiptsDb?.Dispose();
        }

        _trieStoreWatcher?.Dispose();
        Container.Dispose();
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
