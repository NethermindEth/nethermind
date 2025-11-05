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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
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
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Nethermind.Core.Test;
using Nethermind.Xdc.Types;
namespace Nethermind.Xdc.Test.Helpers;


public class XdcTestBlockchain : TestBlockchain
{
    private readonly Random _random = new();

    public static async Task<XdcTestBlockchain> Create(Action<ContainerBuilder>? configurer = null)
    {
        XdcTestBlockchain chain = new();
        await chain.Build(configurer);
        return chain;
    }

    public List<PrivateKey> MasterNodeCandidates { get; }
    public IXdcConsensusContext XdcContext => Container.Resolve<IXdcConsensusContext>();
    public IEpochSwitchManager EpochSwitchManager => _fromXdcContainer.EpochSwitchManager;
    public IQuorumCertificateManager QuorumCertificateManager => _fromXdcContainer.QuorumCertificateManager;
    public ITimeoutCertificateManager TimeoutCertificateManager => _fromXdcContainer.TimeoutCertificateManager;
    public ISnapshotManager SnapshotManager => _fromXdcContainer.SnapshotManager;

    protected XdcTestBlockchain()
    {
        MasterNodeCandidates = new PrivateKeyGenerator().Generate(200).ToList();
    }

    protected Signer Signer => (Signer)_fromXdcContainer.Signer;

    private FromXdcContainer _fromXdcContainer = null!;
    public class FromXdcContainer(
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
        Lazy<ISnapshotManager> snapshotManager,
        Lazy<ISigner> signer
    ) : FromContainer(stateReader, ethereumEcdsa, nonceManager, receiptStorage, txPool, worldStateManager, blockPreprocessorStep, blockTree, blockFinder, logFinder, chainHeadInfoProvider, dbProvider, specProvider, sealEngine, transactionComparerProvider, poSSwitcher, chainLevelInfoRepository, mainProcessingContext, readOnlyTxProcessingEnvFactory, blockProducerEnvFactory, configuration, testBlockchainUtil, poWTestBlockchainUtil, manualTimestamper, blockProductionTrigger, shareableTxProcessorSource, sealer, forkInfo)
    {
        public IEpochSwitchManager EpochSwitchManager => epochSwitchManager.Value;
        public IQuorumCertificateManager QuorumCertificateManager => quorumCertificateManager.Value;
        public ITimeoutCertificateManager TimeoutCertificateManager => timeoutCertificateManager.Value;
        public ISnapshotManager SnapshotManager => snapshotManager.Value;
        public ISigner Signer => signer.Value;
    }
    // Please don't add any new parameter to this method. Pass any customization via autofac's configuration
    // or override method or a utility function that wrap around the autofac configuration.
    // Try to use plugin's module where possible to make sure prod and test components are wired similarly.
    protected override async Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
    {
        JsonSerializer = new EthereumJsonSerializer();

        IConfigProvider configProvider = new ConfigProvider([.. CreateConfigs()]);

        ContainerBuilder builder = ConfigureContainer(new ContainerBuilder(), configProvider);
        ConfigureContainer(builder, configProvider);
        configurer?.Invoke(builder);

        Container = builder.Build();

        _fromXdcContainer = Container.Resolve<FromXdcContainer>();
        _fromContainer = (FromContainer)_fromXdcContainer;

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
            Task newBlockWaiter = BlockTree.WaitForNewBlock(CancellationToken);
            MainProcessingContext.GenesisLoader.Load();
            await newBlockWaiter;
            QuorumCertificateManager.Initialize((XdcBlockHeader)BlockTree.Head!.Header);
        }

        if (testConfiguration.AddBlockOnStart)
            await AddBlocksOnStart();

        return this;
    }

    protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>

        base.ConfigureContainer(builder, configProvider)
            .AddModule(new XdcModuleTestOverrides(configProvider, LimboLogs.Instance))
            .AddSingleton<ISpecProvider>(
            new TestSpecProvider(WrapReleaseSpec(Shanghai.Instance))
            {
                AllowTestChainOverride = false,
            })
            .AddSingleton<Configuration>()
            .AddSingleton<FromContainer>()
            .AddSingleton<FromXdcContainer>()
            .AddScoped<IGenesisBuilder, XdcTestGenesisBuilder>()
            .AddSingleton<IBlockProducer, TestXdcBlockProducer>()
            .AddSingleton((ctx) => new CandidateContainer(MasterNodeCandidates))
            .AddSingleton<ISigner>(ctx =>
            {
                var spec = ctx.Resolve<ISpecProvider>();
                var logmanager = ctx.Resolve<ILogManager>();
                return new Signer(spec.ChainId, MasterNodeCandidates[1], logmanager);
            })

            .AddSingleton((_) => BlockProducer)
            .AddSingleton((_) => BlockProducerRunner)

            .AddSingleton<TestBlockchainUtil.Config, Configuration>((cfg) => new TestBlockchainUtil.Config(cfg.SlotTime))

            .AddSingleton((ctx) => new PoWTestBlockchainUtil(
                ctx.Resolve<IBlockProducerRunner>(),
                ctx.Resolve<IManualBlockProductionTrigger>(),
                ctx.Resolve<ManualTimestamper>(),
                ctx.Resolve<IBlockTree>(),
                ctx.Resolve<ITxPool>(),
                ctx.Resolve<Configuration>().SlotTime
            ));


    private IXdcReleaseSpec WrapReleaseSpec(IReleaseSpec spec)
    {
        var xdcSpec = XdcReleaseSpec.FromReleaseSpec(spec);

        xdcSpec.GenesisMasterNodes = MasterNodeCandidates.Take(30).Select(k => k.Address).ToArray();
        xdcSpec.EpochLength = 900;
        xdcSpec.Gap = 450;
        xdcSpec.SwitchEpoch = 0;
        xdcSpec.SwitchBlock = 0;
        xdcSpec.MasternodeReward = 2.0;   // 2 Ether per masternode
        xdcSpec.ProtectorReward = 1.0;    // 1 Ether per protector
        xdcSpec.ObserverReward = 0.5;     // 0.5 Ether per observer
        xdcSpec.MinimumMinerBlockPerEpoch = 1;
        xdcSpec.LimitPenaltyEpoch = 2;
        xdcSpec.MinimumSigningTx = 1;
        xdcSpec.GasLimitBoundDivisor = 1024;

        V2ConfigParams[] v2ConfigParams = [
            new V2ConfigParams {
                SwitchRound = 0,
                MaxMasternodes = 30,
                CertThreshold = 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 2
            },
            new V2ConfigParams {
                SwitchRound = 5,
                MaxMasternodes = 40,
                CertThreshold = 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 2
            },
            new V2ConfigParams {
                SwitchRound = 10,
                MaxMasternodes = 50,
                CertThreshold = 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 2
            },
            new V2ConfigParams {
                SwitchRound = 15,
                MaxMasternodes = 60,
                CertThreshold = 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 2
            },
            new V2ConfigParams {
                SwitchRound = 20,
                MaxMasternodes = 75,
                CertThreshold = 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 2
            }
        ];

        xdcSpec.V2Configs = v2ConfigParams.ToList();
        return xdcSpec;
    }

    protected override IBlockProducer CreateTestBlockProducer()
    {
        IBlockProducerEnv env = BlockProducerEnvFactory.Create();
        return new TestXdcBlockProducer(
            Signer,
            Container.Resolve<CandidateContainer>(),
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

    private class XdcTestGenesisBuilder(
        ISpecProvider specProvider,
        IWorldState state,
        ISnapshotManager snapshotManager,
        IGenesisPostProcessor[] postProcessors,
        Configuration testConfiguration
    ) : IGenesisBuilder
    {
        public Block Build()
        {
            state.CreateAccount(TestItem.AddressA, testConfiguration.AccountInitialValue);
            state.CreateAccount(TestItem.AddressB, testConfiguration.AccountInitialValue);
            state.CreateAccount(TestItem.AddressC, testConfiguration.AccountInitialValue);

            IXdcReleaseSpec? finalSpec = (IXdcReleaseSpec)specProvider.GetFinalSpec();

            XdcBlockHeaderBuilder xdcBlockHeaderBuilder = new();

            var genesisBlock = new Block(xdcBlockHeaderBuilder
                .WithValidators(finalSpec.GenesisMasterNodes)
                .WithNumber(finalSpec.SwitchBlock)
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
            snapshotManager.StoreSnapshot(new Types.Snapshot(genesisBlock.Number, genesisBlock.Hash!, finalSpec.GenesisMasterNodes));
            return genesisBlock;
        }
    }

    protected override async Task AddBlocksOnStart()
    {
        UInt256 nonce = 0;
        //for (var i = 0; i < MAX_EPOCH_COUNT; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                await AddBlock(CreateTransactionBuilder().WithNonce(nonce++).TestObject);
            }
        }

        while (true)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (BlockTree.Head?.Number == 3) return;
            await Task.Delay(1, CancellationToken);
        }
    }

    public override async Task AddBlock(params Transaction[] transactions)
    {
        await base.AddBlock(transactions);

        var head = (XdcBlockHeader)BlockTree.Head!.Header;
        var headSpec = SpecProvider.GetXdcSpec(head, XdcContext.CurrentRound);

        if (ISnapshotManager.IsTimeforSnapshot(head.Number, headSpec))
        {
            this.SnapshotManager.StoreSnapshot(new Types.Snapshot(head.Number, head.Hash!, MasterNodeCandidates.Select(k => k.Address).ToArray()));
        }

        EpochSwitchInfo switchInfo = EpochSwitchManager.GetEpochSwitchInfo(head.Hash!)!;

        var gap = (ulong)Math.Max(0, switchInfo.EpochSwitchBlockInfo.BlockNumber - switchInfo.EpochSwitchBlockInfo.BlockNumber % headSpec.EpochLength - headSpec.Gap);
        PrivateKey[] masterNodes = TakeRandomMasterNodes(headSpec, switchInfo);
        var headQc = XdcTestHelper.CreateQc(new BlockRoundInfo(head.Hash!, XdcContext.CurrentRound, head.Number), gap,
            masterNodes);
        QuorumCertificateManager.CommitCertificate(headQc);
    }

    public PrivateKey[] TakeRandomMasterNodes(IXdcReleaseSpec headSpec, EpochSwitchInfo switchInfo)
    {
        return switchInfo
                    .Masternodes
                    .OrderBy(x => _random.Next())
                    .Take((int)(Math.Ceiling(switchInfo.Masternodes.Length * headSpec.CertThreshold)))
                    .Select(a => MasterNodeCandidates.First(c => a == c.Address))
                    .ToArray();
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
}
