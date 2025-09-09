// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Find;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.Network;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Blockchain.Blocks;
using Nethermind.Init.Modules;

namespace Nethermind.Core.Test.Blockchain;

public class TestBlockchain : IDisposable
{
    public const int DefaultTimeout = 10000;
    protected long TestTimout { get; init; } = DefaultTimeout;
    public IStateReader StateReader => _fromContainer.StateReader;
    public IEthereumEcdsa EthereumEcdsa => _fromContainer.EthereumEcdsa;
    public INonceManager NonceManager => _fromContainer.NonceManager;
    public ITransactionProcessor TxProcessor => _fromContainer.MainProcessingContext.TransactionProcessor;
    public IMainProcessingContext MainProcessingContext => _fromContainer.MainProcessingContext;
    public IReceiptStorage ReceiptStorage => _fromContainer.ReceiptStorage;
    public ITxPool TxPool => _fromContainer.TxPool;
    public IForkInfo ForkInfo => _fromContainer.ForkInfo;
    public IWorldStateManager WorldStateManager => _fromContainer.WorldStateManager;
    public IReadOnlyTxProcessingEnvFactory ReadOnlyTxProcessingEnvFactory => _fromContainer.ReadOnlyTxProcessingEnvFactory;
    public IShareableTxProcessorSource ShareableTxProcessorSource => _fromContainer.ShareableTxProcessorSource;
    public IBranchProcessor BranchProcessor => _fromContainer.MainProcessingContext.BranchProcessor;
    public IBlockchainProcessor BlockchainProcessor => _fromContainer.MainProcessingContext.BlockchainProcessor;
    public IBlockPreprocessorStep BlockPreprocessorStep => _fromContainer.BlockPreprocessorStep;

    public IBlockTree BlockTree => _fromContainer.BlockTree;

    public IBlockFinder BlockFinder => _fromContainer.BlockFinder;

    public ILogFinder LogFinder => _fromContainer.LogFinder;
    public IJsonSerializer JsonSerializer { get; set; } = null!;
    public IReadOnlyStateProvider ReadOnlyState => ChainHeadInfoProvider.ReadOnlyStateProvider;
    public IChainHeadInfoProvider ChainHeadInfoProvider => _fromContainer.ChainHeadInfoProvider;
    public IDb StateDb => DbProvider.StateDb;
    public IBlockProducer BlockProducer { get; protected set; } = null!;
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

    protected AutoCancelTokenSource _cts;
    public CancellationToken CancellationToken => _cts.Token;

    private TestBlockchainUtil TestUtil => _fromContainer.TestBlockchainUtil.Value;
    private PoWTestBlockchainUtil PoWTestUtil => _fromContainer.PoWTestBlockchainUtil.Value;
    private IManualBlockProductionTrigger BlockProductionTrigger => _fromContainer.BlockProductionTrigger;

    public ManualTimestamper Timestamper => _fromContainer.ManualTimestamper;
    protected IBlocksConfig BlocksConfig => Container.Resolve<IBlocksConfig>();

    public ProducedBlockSuggester Suggester { get; protected set; } = null!;

    public IExecutionRequestsProcessor MainExecutionRequestsProcessor => ((MainProcessingContext)_fromContainer.MainProcessingContext).LifetimeScope.Resolve<IExecutionRequestsProcessor>();
    public IChainLevelInfoRepository ChainLevelInfoRepository => _fromContainer.ChainLevelInfoRepository;

    protected IBlockProducerEnvFactory BlockProducerEnvFactory => _fromContainer.BlockProducerEnvFactory;
    protected ISealer Sealer => _fromContainer.Sealer;

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
        IChainHeadInfoProvider ChainHeadInfoProvider,
        IDbProvider DbProvider,
        ISpecProvider SpecProvider,
        ISealEngine SealEngine,
        ITransactionComparerProvider TransactionComparerProvider,
        IPoSSwitcher PoSSwitcher,
        IChainLevelInfoRepository ChainLevelInfoRepository,
        IMainProcessingContext MainProcessingContext,
        IReadOnlyTxProcessingEnvFactory ReadOnlyTxProcessingEnvFactory,
        IBlockProducerEnvFactory BlockProducerEnvFactory,
        Configuration Configuration,
        Lazy<TestBlockchainUtil> TestBlockchainUtil,
        Lazy<PoWTestBlockchainUtil> PoWTestBlockchainUtil,
        ManualTimestamper ManualTimestamper,
        IManualBlockProductionTrigger BlockProductionTrigger,
        IShareableTxProcessorSource ShareableTxProcessorSource,
        ISealer Sealer,
        IForkInfo ForkInfo
    )
    {
    }

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
        JsonSerializer = new EthereumJsonSerializer();

        IConfigProvider configProvider = new ConfigProvider([.. CreateConfigs()]);

        ContainerBuilder builder = ConfigureContainer(new ContainerBuilder(), configProvider);
        ConfigureContainer(builder, configProvider);
        configurer?.Invoke(builder);

        Container = builder.Build();
        _fromContainer = Container.Resolve<FromContainer>();

        IWorldState state = _fromContainer.WorldStateManager.GlobalWorldState;

        ISpecProvider specProvider = SpecProvider;
        Configuration testConfiguration = _fromContainer.Configuration;

        BlockchainProcessor.Start();

        BlockProducer = CreateTestBlockProducer();
        BlockProducerRunner ??= CreateBlockProducerRunner();
        BlockProducerRunner.Start();
        Suggester = new ProducedBlockSuggester(BlockTree, BlockProducerRunner);

        _cts = AutoCancelTokenSource.ThatCancelAfter(Debugger.IsAttached ? TimeSpan.FromMilliseconds(-1) : TimeSpan.FromMilliseconds(TestTimout));

        if (testConfiguration.SuggestGenesisOnStart) MainProcessingContext.GenesisLoader.Load();

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
            .AddSingleton<ManualTimestamper>(new ManualTimestamper(InitialTimestamp))
            .AddSingleton<Configuration>()
            .AddSingleton<FromContainer>()
            .AddScoped<IGenesisBuilder, TestGenesisBuilder>()

            // Some validator configurations
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<IUnclesValidator>(Always.Valid)
            .AddSingleton<ISealer>(new NethDevSealEngine(TestItem.AddressD))

            .AddSingleton<IBlockProducer>((_) => this.BlockProducer)
            .AddSingleton<IBlockProducerRunner>((_) => this.BlockProducerRunner)

            .AddSingleton<TestBlockchainUtil>((ctx) => new TestBlockchainUtil(
                ctx.Resolve<IBlockProducer>(),
                ctx.Resolve<ManualTimestamper>(),
                ctx.Resolve<IBlockTree>(),
                ctx.Resolve<ITxPool>(),
                ctx.Resolve<Configuration>().SlotTime
            ))

            .AddSingleton<PoWTestBlockchainUtil>((ctx) => new PoWTestBlockchainUtil(
                ctx.Resolve<IBlockProducerRunner>(),
                ctx.Resolve<IManualBlockProductionTrigger>(),
                ctx.Resolve<ManualTimestamper>(),
                ctx.Resolve<IBlockTree>(),
                ctx.Resolve<ITxPool>(),
                ctx.Resolve<Configuration>().SlotTime
            ))
    ;

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

    protected virtual IBlockProducer CreateTestBlockProducer()
    {
        IBlockProducerEnv env = BlockProducerEnvFactory.Create();
        return new TestBlockProducer(
            env.TxSource,
            env.ChainProcessor,
            env.ReadOnlyStateProvider,
            Sealer,
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

    public ILogManager LogManager => Container.Resolve<ILogManager>();

    private class TestGenesisBuilder(
        ISpecProvider specProvider,
        IWorldState state,
        IGenesisPostProcessor[] postProcessors,
        Configuration testConfiguration
    ) : IGenesisBuilder
    {
        public Block Build()
        {
            // Eip4788 precompile state account
            if (specProvider.GenesisSpec.IsBeaconBlockRootAvailable)
            {
                state.CreateAccount(specProvider.GenesisSpec.Eip4788ContractAddress!, 1);
            }

            // Eip2935
            if (specProvider.GenesisSpec.IsBlockHashInStateAvailable)
            {
                state.CreateAccount(specProvider.GenesisSpec.Eip2935ContractAddress, 1);
            }

            state.CreateAccount(TestItem.AddressA, testConfiguration.AccountInitialValue);
            state.CreateAccount(TestItem.AddressB, testConfiguration.AccountInitialValue);
            state.CreateAccount(TestItem.AddressC, testConfiguration.AccountInitialValue);

            byte[] code = Bytes.FromHexString("0xabcd");
            state.InsertCode(TestItem.AddressA, code, specProvider.GenesisSpec!);
            state.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));

            IReleaseSpec? finalSpec = specProvider.GetFinalSpec();

            if (finalSpec?.WithdrawalsEnabled is true)
            {
                state.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
                state.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, specProvider.GenesisSpec!);
            }

            if (finalSpec?.ConsolidationRequestsEnabled is true)
            {
                state.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
                state.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, specProvider.GenesisSpec!);
            }

            BlockBuilder genesisBlockBuilder = Builders.Build.A.Block.Genesis;

            if (specProvider.SealEngine == Core.SealEngineType.AuRa)
            {
                genesisBlockBuilder.WithAura(0, new byte[65]);
            }

            if (specProvider.GenesisSpec.IsEip4844Enabled)
            {
                genesisBlockBuilder.WithBlobGasUsed(0);
                genesisBlockBuilder.WithExcessBlobGas(0);
            }

            if (specProvider.GenesisSpec.IsBeaconBlockRootAvailable)
            {
                genesisBlockBuilder.WithParentBeaconBlockRoot(Keccak.Zero);
            }

            if (specProvider.GenesisSpec.RequestsEnabled)
            {
                genesisBlockBuilder.WithEmptyRequestsHash();
            }

            Block genesisBlock = genesisBlockBuilder.TestObject;

            foreach (IGenesisPostProcessor genesisPostProcessor in postProcessors)
            {
                genesisPostProcessor.PostProcess(genesisBlock);
            }

            state.Commit(specProvider.GenesisSpec!);
            state.CommitTree(0);
            genesisBlock.Header.StateRoot = state.StateRoot;
            genesisBlock.Header.Hash = genesisBlock.Header.CalculateHash();
            return genesisBlock;
        }
    }

    protected virtual async Task AddBlocksOnStart()
    {
        await AddBlock();
        await AddBlock(CreateTransactionBuilder().WithNonce(0).TestObject);
        await AddBlock(CreateTransactionBuilder().WithNonce(1).TestObject, CreateTransactionBuilder().WithNonce(2).TestObject);
    }

    private TransactionBuilder<Transaction> CreateTransactionBuilder()
    {
        TransactionBuilder<Transaction> txBuilder = BuildSimpleTransaction;

        Block? head = BlockFinder.Head;
        if (head is not null)
        {
            IReleaseSpec headReleaseSpec = SpecProvider.GetSpec(head.Header);

            if (headReleaseSpec.IsEip1559Enabled && headReleaseSpec.Eip1559TransitionBlock <= head.Number)
            {
                UInt256 nextFee = headReleaseSpec.BaseFeeCalculator.Calculate(head.Header, headReleaseSpec);
                txBuilder = txBuilder
                    .WithType(TxType.EIP1559)
                    .WithMaxFeePerGasIfSupports1559(nextFee * 2);
            }
        }

        return txBuilder;
    }

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

    public virtual async Task AddBlock(params Transaction[] transactions)
    {
        await TestUtil.AddBlockAndWaitForHead(false, _cts.Token, transactions);
    }

    public async Task AddBlockMayMissTx(params Transaction[] transactions)
    {
        await TestUtil.AddBlockAndWaitForHead(true, _cts.Token, transactions);
    }

    public async Task AddBlockThroughPoW(params Transaction[] transactions)
    {
        await PoWTestUtil.AddBlockAndWaitForHead(_cts.Token, transactions);
    }

    public async Task AddBlockDoNotWaitForHead(params Transaction[] transactions)
    {
        await TestUtil.AddBlockDoNotWaitForHead(false, _cts.Token, transactions);
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
        await AddBlockMayMissTx(GetFundsTransaction(address, ether));

    public async Task AddFunds(params (Address address, UInt256 ether)[] funds) =>
        await AddBlock(funds.Select((f, i) => GetFundsTransaction(f.address, f.ether, (uint)i)).ToArray());

    public async Task AddFundsAfterLondon(params (Address address, UInt256 ether)[] funds) =>
        await AddBlock(funds.Select((f, i) => GetFunds1559Transaction(f.address, f.ether, (uint)i)).ToArray());

    private Transaction GetFundsTransaction(Address address, UInt256 ether, uint index = 0)
    {
        UInt256 nonce = StateReader.GetNonce(BlockTree.Head!.Header, TestItem.AddressA);
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
        UInt256 nonce = StateReader.GetNonce(BlockTree.Head!.Header, TestItem.AddressA);
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
}
