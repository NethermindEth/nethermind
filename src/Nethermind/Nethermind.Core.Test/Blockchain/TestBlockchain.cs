// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Utils;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Find;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Blockchain;

public class TestBlockchain : IDisposable
{
    public const int DefaultTimeout = 10000;
    protected long TestTimout { get; init; } = DefaultTimeout;
    public IStateReader StateReader => _fromContainer.StateReader;
    public IEthereumEcdsa EthereumEcdsa => _fromContainer.EthereumEcdsa;
    public INonceManager NonceManager => _fromContainer.NonceManager;
    public ITransactionProcessor TxProcessor => _fromContainer.MainProcessingContext.TransactionProcessor;
    public IReceiptStorage ReceiptStorage => _fromContainer.ReceiptStorage;
    public ITxPool TxPool => _fromContainer.TxPool;
    public IWorldStateManager WorldStateManager => _fromContainer.WorldStateManager;
    public IReadOnlyTxProcessingEnvFactory ReadOnlyTxProcessingEnvFactory => _fromContainer.ReadOnlyTxProcessingEnvFactory;
    public IBlockProcessor BlockProcessor { get; set; } = null!;
    public IBlockchainProcessor BlockchainProcessor { get; set; } = null!;

    public IBlockPreprocessorStep BlockPreprocessorStep => _fromContainer.BlockPreprocessorStep;

    public IBlockProcessingQueue BlockProcessingQueue { get; set; } = null!;
    public IBlockTree BlockTree => _fromContainer.BlockTree;

    public Action<IWorldState>? InitialStateMutator { get; set; }

    public IBlockFinder BlockFinder => _fromContainer.BlockFinder;

    public ILogFinder LogFinder => _fromContainer.LogFinder;
    public IJsonSerializer JsonSerializer { get; set; } = null!;
    public IReadOnlyStateProvider ReadOnlyState => _fromContainer.ReadOnlyState;
    public IDb StateDb => DbProvider.StateDb;
    public IDb BlocksDb => DbProvider.BlocksDb;
    public IBlockProducer BlockProducer { get; private set; } = null!;
    public IBlockProducerRunner BlockProducerRunner { get; protected set; } = null!;
    public IDbProvider DbProvider => _fromContainer.DbProvider;
    public ISpecProvider SpecProvider => _fromContainer.SpecProvider;

    public ISealEngine SealEngine => _fromContainer.SealEngine;

    public ITransactionComparerProvider TransactionComparerProvider => _fromContainer.TransactionComparerProvider;

    public IPoSSwitcher PoSSwitcher => _fromContainer.PoSSwitcher;

    protected TestBlockchain()
    {
    }

    public string SealEngineType { get; set; } = null!;

    public static readonly Address AccountA = TestItem.AddressA;
    public static readonly Address AccountB = TestItem.AddressB;
    public static readonly Address AccountC = TestItem.AddressC;

    public static readonly DateTime InitialTimestamp = new(2020, 2, 15, 12, 50, 30, DateTimeKind.Utc);

    public static readonly UInt256 InitialValue = 1000.Ether();
    public IHeaderValidator HeaderValidator => _fromContainer.HeaderValidator;

    protected AutoCancelTokenSource _cts;
    public CancellationToken CancellationToken => _cts.Token;
    private TestBlockchainUtil _testUtil = null!;

    public IBlockValidator BlockValidator => _fromContainer.BlockValidator;

    public BuildBlocksWhenRequested BlockProductionTrigger { get; } = new();

    public ManualTimestamper Timestamper { get; private set; } = null!;
    public BlocksConfig BlocksConfig { get; protected set; } = new();

    public ProducedBlockSuggester Suggester { get; protected set; } = null!;

    public IExecutionRequestsProcessor MainExecutionRequestsProcessor => ((MainBlockProcessingContext)_fromContainer.MainProcessingContext).LifetimeScope.Resolve<IExecutionRequestsProcessor>();
    public IChainLevelInfoRepository ChainLevelInfoRepository => _fromContainer.ChainLevelInfoRepository;

    public static TransactionBuilder<Transaction> BuildSimpleTransaction => Builders.Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).To(AccountB);

    public IContainer Container { get; set; } = null!;
    private ChainSpec? _chainSpec = null!;
    protected ChainSpec ChainSpec => _chainSpec ??= CreateChainSpec();

    // Resolving all these component at once is faster.
    private FromContainer _fromContainer = null!;
    private record FromContainer(
        IStateReader StateReader,
        IEthereumEcdsa EthereumEcdsa,
        INonceManager NonceManager,
        IReceiptStorage ReceiptStorage,
        ITxPool TxPool,
        IWorldStateManager WorldStateManager,
        IBlockPreprocessorStep BlockPreprocessorStep,
        IBlockTree BlockTree,
        IBlockFinder BlockFinder,
        ILogFinder LogFinder,
        IReadOnlyStateProvider ReadOnlyState,
        IDbProvider DbProvider,
        ISpecProvider SpecProvider,
        ISealEngine SealEngine,
        ITransactionComparerProvider TransactionComparerProvider,
        IPoSSwitcher PoSSwitcher,
        IHeaderValidator HeaderValidator,
        IBlockValidator BlockValidator,
        IChainLevelInfoRepository ChainLevelInfoRepository,
        IMainProcessingContext MainProcessingContext,
        IReadOnlyTxProcessingEnvFactory ReadOnlyTxProcessingEnvFactory,
        Configuration Configuration,
        ISealer Sealer
    );

    public class Configuration
    {
        public bool SuggestGenesisOnStart = true;
        public bool AddBlockOnStart = true;
        public UInt256 AccountInitialValue = InitialValue;
        public long SlotTime = 1;
    }

    // Please don't add any new parameter to this method. Pass any customization via autofac's configuration
    // or override method or a utility function that wrap around the autofac configuration.
    // Try to use plugin's module where possible to make sure prod and test components are wired similarly.
    protected virtual async Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
    {
        Timestamper = new ManualTimestamper(InitialTimestamp);
        JsonSerializer = new EthereumJsonSerializer();

        IConfigProvider configProvider = new ConfigProvider(CreateConfigs().ToArray());

        ContainerBuilder builder = ConfigureContainer(new ContainerBuilder(), configProvider);
        ConfigureContainer(builder, configProvider);
        configurer?.Invoke(builder);

        Container = builder.Build();
        _fromContainer = Container.Resolve<FromContainer>();

        IWorldState state = _fromContainer.WorldStateManager.GlobalWorldState;

        ISpecProvider specProvider = SpecProvider;
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

        Configuration testConfiguration = _fromContainer.Configuration;
        state.CreateAccount(TestItem.AddressA, testConfiguration.AccountInitialValue);
        state.CreateAccount(TestItem.AddressB, testConfiguration.AccountInitialValue);
        state.CreateAccount(TestItem.AddressC, testConfiguration.AccountInitialValue);

        InitialStateMutator?.Invoke(state);

        byte[] code = Bytes.FromHexString("0xabcd");
        state.InsertCode(TestItem.AddressA, code, SpecProvider.GenesisSpec);
        state.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));

        IReleaseSpec? finalSpec = specProvider?.GetFinalSpec();

        if (finalSpec?.WithdrawalsEnabled is true)
        {
            state.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
            state.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, SpecProvider.GenesisSpec);
        }

        if (finalSpec?.ConsolidationRequestsEnabled is true)
        {
            state.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
            state.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, SpecProvider.GenesisSpec);
        }

        state.Commit(SpecProvider.GenesisSpec);
        state.CommitTree(0);

        BlockProcessor = CreateBlockProcessor(WorldStateManager.GlobalWorldState);

        BlockchainProcessor chainProcessor = new(BlockTree, BlockProcessor, BlockPreprocessorStep, StateReader, LogManager, Consensus.Processing.BlockchainProcessor.Options.Default);
        BlockchainProcessor = chainProcessor;
        BlockProcessingQueue = chainProcessor;
        chainProcessor.Start();

        ITxSource txPoolTxSource = Container.Resolve<ITxSource>();
        ITransactionComparerProvider transactionComparerProvider = new TransactionComparerProvider(SpecProvider, BlockFinder);
        BlockProducer = CreateTestBlockProducer(txPoolTxSource, _fromContainer.Sealer, transactionComparerProvider);
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
            testConfiguration.SlotTime
        );

        if (testConfiguration.SuggestGenesisOnStart)
        {
            Task waitGenesis = WaitForNewHead();
            Block? genesis = GetGenesisBlock(WorldStateManager.GlobalWorldState);
            BlockTree.SuggestBlock(genesis);
            await waitGenesis;
        }

        if (testConfiguration.AddBlockOnStart)
            await AddBlocksOnStart();

        return this;
    }

    protected virtual ChainSpec CreateChainSpec()
    {
        return new ChainSpec();
    }

    protected virtual ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
        builder
            .AddModule(new PseudoNethermindModule(ChainSpec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, Random.Shared.Next().ToString()))
            .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
            .AddDecorator<ISpecProvider>((ctx, specProvider) => WrapSpecProvider(specProvider))
            .AddSingleton<Configuration>()
            .AddSingleton<FromContainer>()

            // Some validator configurations
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<IUnclesValidator>(Always.Valid)
            .AddSingleton<ISealer>(new NethDevSealEngine(TestItem.AddressD));

    protected virtual IEnumerable<IConfig> CreateConfigs()
    {
        return [new BlocksConfig()
        {
            MinGasPrice = 0 // Tx pool test seems to need this.
        }];
    }

    private static ISpecProvider WrapSpecProvider(ISpecProvider specProvider)
    {
        return specProvider is TestSpecProvider { AllowTestChainOverride: false }
            ? specProvider
            : new OverridableSpecProvider(specProvider, static s => new OverridableReleaseSpec(s) { IsEip3607Enabled = false });
    }

    protected virtual IBlockProducer CreateTestBlockProducer(ITxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
    {
        BlockProducerEnvFactory blockProducerEnvFactory = new(
            WorldStateManager,
            ReadOnlyTxProcessingEnvFactory,
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
            new BeaconBlockRootHandler(TxProcessor, state),
            new BlockhashStore(SpecProvider, state),
            LogManager,
            new WithdrawalProcessor(state, LogManager),
            MainExecutionRequestsProcessor,
            preWarmer: CreateBlockCachePreWarmer());


    protected IBlockCachePreWarmer CreateBlockCachePreWarmer() =>
        new BlockCachePreWarmer(
            ReadOnlyTxProcessingEnvFactory,
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

public static class ContainerBuilderExtensions
{
    public static ContainerBuilder ConfigureTestConfiguration(this ContainerBuilder builder, Action<TestBlockchain.Configuration> configurer)
    {
        return builder.AddDecorator<TestBlockchain.Configuration>((ctx, conf) =>
        {
            configurer(conf);
            return conf;
        });
    }

    /// <summary>
    /// Some test require exposed `TrieStore` and `IPruningTrieStore` which is not normally exposed at all
    /// hidden in `PruningTrieStateFactory`. So this create mini state configuration for that.
    /// It does not cover the full standard world state configuration though, so not for general use.
    /// </summary>
    public static ContainerBuilder ConfigureTrieStoreExposedWorldStateManager(this ContainerBuilder builder)
    {
        return builder
            // Need to manually create the WorldStateManager to expose the triestore which is normally hidden by PruningTrieStateFactory
            // This means it does not use pruning triestore by default though which is potential edge case.
            .AddSingleton<TrieStore>(ctx => TestTrieStoreFactory.Build(ctx.Resolve<IDbProvider>().StateDb, LimboLogs.Instance))
            .Bind<IPruningTrieStore, TrieStore>()
            .AddSingleton<IWorldStateManager>(ctx =>
            {
                IDbProvider dbProvider = ctx.Resolve<IDbProvider>();
                TrieStore trieStore = ctx.Resolve<TrieStore>();
                PreBlockCaches preBlockCaches = new PreBlockCaches();
                WorldState worldState = new WorldState(trieStore, dbProvider.CodeDb, LimboLogs.Instance,
                    preBlockCaches: preBlockCaches);
                return new WorldStateManager(worldState, trieStore, dbProvider, LimboLogs.Instance);
            })
            .AddSingleton<TrieStoreBoundaryWatcher>() // Normally not exposed also
            .ResolveOnServiceActivation<TrieStoreBoundaryWatcher, IWorldStateManager>();
    }
}
