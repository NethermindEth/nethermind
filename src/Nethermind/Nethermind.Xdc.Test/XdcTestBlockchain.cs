// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
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
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Find;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Xdc;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test;
using Nethermind.Xdc.Test.Modules;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Test.Blockchain;

public class XdcTestBlockchain : IDisposable
{
    public static async Task<XdcTestBlockchain> Create(Action<ContainerBuilder>? configurer = null)
    {
        XdcTestBlockchain chain = new();
        await chain.Build(configurer);
        return chain;
    }

    public const int DefaultTimeout = 10000;
    protected long TestTimout { get; init; } = DefaultTimeout;

    public TestMasternodeSmartContaractEmulator MasterNodesRotator = TestMasternodeSmartContaractEmulator.Instance;
    public XdcContext XdcContext => Container.Resolve<XdcContext>();

    public IEpochSwitchManager EpochSwitchManager => _fromContainer.EpochSwitchManager;
    public IQuorumCertificateManager QuorumCertificateManager => _fromContainer.QuorumCertificateManager;
    public ITimeoutCertificateManager TimeoutCertificateManager => _fromContainer.TimeoutCertificateManager;
    public ISnapshotManager SnapshotManager => _fromContainer.SnapshotManager;

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
    public IBlockProcessingQueue BlockProcessingQueue => _fromContainer.MainProcessingContext.BlockProcessingQueue;
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

    protected XdcTestBlockchain()
    {
    }

    public string SealEngineType { get; set; } = null!;

    public static readonly Address AccountA = TestItem.AddressA;
    public static readonly Address AccountB = TestItem.AddressB;
    public static readonly Address AccountC = TestItem.AddressC;

    public static readonly DateTime InitialTimestamp = new(2020, 2, 15, 12, 50, 30, DateTimeKind.Utc);

    public static readonly UInt256 InitialValue = 1000.Ether();
    private readonly int MAX_EPOCH_COUNT = 10;
    protected AutoCancelTokenSource _cts;
    public CancellationToken CancellationToken => _cts.Token;

    private TestBlockchainUtil TestUtil => _fromContainer.TestBlockchainUtil;
    private PoWTestBlockchainUtil PoWTestUtil => _fromContainer.PoWTestBlockchainUtil;
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
    public class FromContainer(
        Lazy<IStateReader> stateReader,
        Lazy<IEthereumEcdsa> ethereumEcdsa,
        Lazy<INonceManager> nonceManager,
        Lazy<IReceiptStorage> receiptStorage,
        Lazy<ITxPool> txPool,
        Lazy<IWorldStateManager> worldStateManager,
        Lazy<IBlockPreprocessorStep> blockPreprocessorStep,
        Lazy<IBlockTree> blockTree,
        Lazy<IBlockFinder> blockFinder,
        Lazy<ILogFinder> logFinder,
        Lazy<IChainHeadInfoProvider> chainHeadInfoProvider,
        Lazy<IDbProvider> dbProvider,
        Lazy<ISpecProvider> specProvider,
        Lazy<ISealEngine> sealEngine,
        Lazy<ITransactionComparerProvider> transactionComparerProvider,
        Lazy<IPoSSwitcher> poSSwitcher,
        Lazy<IChainLevelInfoRepository> chainLevelInfoRepository,
        Lazy<IMainProcessingContext> mainProcessingContext,
        Lazy<IReadOnlyTxProcessingEnvFactory> readOnlyTxProcessingEnvFactory,
        Lazy<IBlockProducerEnvFactory> blockProducerEnvFactory,
        Lazy<Configuration> configuration,
        Lazy<TestBlockchainUtil> testBlockchainUtil,
        Lazy<PoWTestBlockchainUtil> poWTestBlockchainUtil,
        Lazy<ManualTimestamper> manualTimestamper,
        Lazy<IManualBlockProductionTrigger> blockProductionTrigger,
        Lazy<IShareableTxProcessorSource> shareableTxProcessorSource,
        Lazy<ISealer> sealer,
        Lazy<IForkInfo> forkInfo,
        Lazy<IEpochSwitchManager> epochSwitchManager,
        Lazy<IQuorumCertificateManager> quorumCertificateManager,
        Lazy<ITimeoutCertificateManager> timeoutCertificateManager,
        Lazy<ISnapshotManager> snapshotManager
    )
    {
        public IStateReader StateReader => stateReader.Value;
        public IEthereumEcdsa EthereumEcdsa => ethereumEcdsa.Value;
        public INonceManager NonceManager => nonceManager.Value;
        public IReceiptStorage ReceiptStorage => receiptStorage.Value;
        public ITxPool TxPool => txPool.Value;
        public IWorldStateManager WorldStateManager => worldStateManager.Value;
        public IBlockPreprocessorStep BlockPreprocessorStep => blockPreprocessorStep.Value;
        public IBlockTree BlockTree => blockTree.Value;
        public IBlockFinder BlockFinder => blockFinder.Value;
        public ILogFinder LogFinder => logFinder.Value;
        public IChainHeadInfoProvider ChainHeadInfoProvider => chainHeadInfoProvider.Value;
        public IDbProvider DbProvider => dbProvider.Value;
        public ISpecProvider SpecProvider => specProvider.Value;
        public ISealEngine SealEngine => sealEngine.Value;
        public ITransactionComparerProvider TransactionComparerProvider => transactionComparerProvider.Value;
        public IPoSSwitcher PoSSwitcher => poSSwitcher.Value;
        public IChainLevelInfoRepository ChainLevelInfoRepository => chainLevelInfoRepository.Value;
        public IMainProcessingContext MainProcessingContext => mainProcessingContext.Value;
        public IReadOnlyTxProcessingEnvFactory ReadOnlyTxProcessingEnvFactory => readOnlyTxProcessingEnvFactory.Value;
        public IBlockProducerEnvFactory BlockProducerEnvFactory => blockProducerEnvFactory.Value;
        public Configuration Configuration => configuration.Value;
        public TestBlockchainUtil TestBlockchainUtil => testBlockchainUtil.Value;
        public PoWTestBlockchainUtil PoWTestBlockchainUtil => poWTestBlockchainUtil.Value;
        public ManualTimestamper ManualTimestamper => manualTimestamper.Value;
        public IManualBlockProductionTrigger BlockProductionTrigger => blockProductionTrigger.Value;
        public IShareableTxProcessorSource ShareableTxProcessorSource => shareableTxProcessorSource.Value;
        public ISealer Sealer => sealer.Value;
        public IForkInfo ForkInfo => forkInfo.Value;

        public IEpochSwitchManager EpochSwitchManager => epochSwitchManager.Value;
        public IQuorumCertificateManager QuorumCertificateManager => quorumCertificateManager.Value;
        public ITimeoutCertificateManager TimeoutCertificateManager => timeoutCertificateManager.Value;

        public ISnapshotManager SnapshotManager => snapshotManager.Value;
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
    protected virtual async Task<XdcTestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
    {
        JsonSerializer = new EthereumJsonSerializer();

        IConfigProvider configProvider = new ConfigProvider([.. CreateConfigs()]);

        ContainerBuilder builder = ConfigureContainer(new ContainerBuilder(), configProvider);
        ConfigureContainer(builder, configProvider);
        configurer?.Invoke(builder);

        Container = builder.Build();
        _fromContainer = Container.Resolve<FromContainer>();

        Configuration testConfiguration = _fromContainer.Configuration;

        BlockchainProcessor.Start();

        BlockProducer = CreateTestBlockProducer();
        BlockProducerRunner ??= CreateBlockProducerRunner();
        BlockProducerRunner.Start();
        Suggester = new ProducedBlockSuggester(BlockTree, BlockProducerRunner);

        _cts = AutoCancelTokenSource.ThatCancelAfter(Debugger.IsAttached ? TimeSpan.FromMilliseconds(-1) : TimeSpan.FromMilliseconds(TestTimout));

        if (testConfiguration.SuggestGenesisOnStart)
        {
            // The block added event is not waited by genesis, but its needed to wait here so that `AddBlock` wait correctly.
            Task newBlockWaiter = BlockTree.WaitForNewBlock(this.CancellationToken);
            MainProcessingContext.GenesisLoader.Load();
            await newBlockWaiter;
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
            .AddModule(new PseudoXdcModule(configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, Random.Shared.Next().ToString()))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(WrapReleaseSpec(Frontier.Instance)))
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

            .AddSingleton<TestBlockchainUtil.Config, Configuration>((cfg) => new TestBlockchainUtil.Config(cfg.SlotTime))

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

    private static IXdcReleaseSpec WrapReleaseSpec(IReleaseSpec spec)
    {
        XdcReleaseSpec xdcSpec = XdcReleaseSpec.FromReleaseSpec(spec);

        xdcSpec.EpochLength = 5;
        xdcSpec.Gap = 1;
        xdcSpec.SwitchEpoch = 0;
        xdcSpec.SwitchBlock = UInt256.Zero;
        xdcSpec.MasternodeReward = 2.0;   // 2 Ether per masternode
        xdcSpec.ProtectorReward = 1.0;    // 1 Ether per protector
        xdcSpec.ObserverReward = 0.5;     // 0.5 Ether per observer
        xdcSpec.MinimumMinerBlockPerEpoch = 1;
        xdcSpec.LimitPenaltyEpoch = 2;
        xdcSpec.MinimumSigningTx = 1;

        V2ConfigParams[] v2ConfigParams = [
            new V2ConfigParams {
                SwitchRound = 0,
                MaxMasternodes = 30,
                CertThreshold = 0.7,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 1000
            },
            new V2ConfigParams {
                SwitchRound = 5,
                MaxMasternodes = 40,
                CertThreshold = 0.7,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 1000
            },
            new V2ConfigParams {
                SwitchRound = 10,
                MaxMasternodes = 50,
                CertThreshold = 0.7,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 1000
            },
            new V2ConfigParams {
                SwitchRound = 15,
                MaxMasternodes = 60,
                CertThreshold = 0.7,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 1000
            },
            new V2ConfigParams {
                SwitchRound = 20,
                MaxMasternodes = 75,
                CertThreshold = 0.7,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 1000
            }
        ];

        xdcSpec.V2Configs = v2ConfigParams.ToList();
        return xdcSpec;
    }

    protected virtual IBlockProducer CreateTestBlockProducer()
    {
        IBlockProducerEnv env = BlockProducerEnvFactory.Create();
        return new Xdc.XdcBlockProducer(
            EpochSwitchManager,
            SnapshotManager,
            XdcContext,
            env.TxSource,
            env.ChainProcessor,
            Sealer,
            BlockTree,
            env.ReadOnlyStateProvider,
            new FollowOtherMiners(SpecProvider),
            Timestamper,
            SpecProvider,
            LogManager,
            ConstantDifficulty.One,
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
            state.CreateAccount(TestItem.AddressA, testConfiguration.AccountInitialValue);
            state.CreateAccount(TestItem.AddressB, testConfiguration.AccountInitialValue);
            state.CreateAccount(TestItem.AddressC, testConfiguration.AccountInitialValue);

            byte[] code = Bytes.FromHexString("0xabcd");
            state.InsertCode(TestItem.AddressA, code, specProvider.GenesisSpec!);
            state.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));

            IReleaseSpec? finalSpec = specProvider.GetFinalSpec();

            XdcBlockHeaderBuilder xdcBlockHeaderBuilder = new();

            Block genesisBlock = new Block(xdcBlockHeaderBuilder
                .WithNumber(0)
                .WithGasUsed(0)
                .TestObject);

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
        await AddSwitchBlock();

        for(int i = 0; i < MAX_EPOCH_COUNT; i++)
        {
            await AddBlock();
            await AddBlock(CreateTransactionBuilder().WithNonce(0).TestObject);
            await AddBlock(CreateTransactionBuilder().WithNonce(1).TestObject, CreateTransactionBuilder().WithNonce(2).TestObject);
        }

        while (true)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (BlockTree.Head?.Number == 3) return;
            await Task.Delay(1, CancellationToken);
        }
    }

    private async Task AddSwitchBlock()
    {
        // switch block is the first block of the new consensus and it is our genesis block we wont have any blocks before it, it requires special handling here
        Block? head = BlockFinder.Head;
        if (head is null)
        {
            await AddBlock();
        }
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

    public async Task AddBlock(TestBlockchainUtil.AddBlockFlags flags, params Transaction[] transactions)
    {
        await TestUtil.AddBlock(flags, _cts.Token, transactions);
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
