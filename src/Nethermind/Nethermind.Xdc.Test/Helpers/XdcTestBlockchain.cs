// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Utils;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Facade.Find;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.Helpers;


public class XdcTestBlockchain : TestBlockchain
{
    private readonly Random _random = new();
    private readonly bool _useHotStuffModule;

    public static async Task<XdcTestBlockchain> Create(int blocksToAdd = 3, bool useHotStuffModule = false, Action<ContainerBuilder>? configurer = null)
    {
        XdcTestBlockchain chain = new(useHotStuffModule);
        await chain.Build(configurer);

        var fromXdcContainer = (FromContainer)chain.Container.Resolve<FromXdcContainer>();

        Configuration testConfiguration = fromXdcContainer.Configuration;

        if (testConfiguration.SuggestGenesisOnStart)
        {
            // The block added event is not waited by genesis, but its needed to wait here so that `AddBlock` wait correctly.
            Task newBlockWaiter = chain.BlockTree.WaitForNewBlock(chain.CancellationToken);
            chain.MainProcessingContext.GenesisLoader.Load();
            await newBlockWaiter;
            chain.QuorumCertificateManager.Initialize((XdcBlockHeader)chain.BlockTree.Head!.Header);
        }

        await chain.AddBlocks(blocksToAdd, true);

        return chain;
    }

    public List<PrivateKey> MasterNodeCandidates { get; }
    public List<PrivateKey> RandomKeys { get; }
    public IXdcConsensusContext XdcContext => Container.Resolve<IXdcConsensusContext>();
    public IEpochSwitchManager EpochSwitchManager => _fromXdcContainer.EpochSwitchManager;
    public IQuorumCertificateManager QuorumCertificateManager => _fromXdcContainer.QuorumCertificateManager;
    public ITimeoutCertificateManager TimeoutCertificateManager => _fromXdcContainer.TimeoutCertificateManager;
    public ISnapshotManager SnapshotManager => _fromXdcContainer.SnapshotManager;
    public IVotesManager VotesManager => _fromXdcContainer.VotesManager;
    internal TestRandomSigner RandomSigner { get; }
    internal XdcHotStuff ConsensusModule => (XdcHotStuff)BlockProducerRunner;


    protected XdcTestBlockchain(bool useHotStuffModule)
    {
        var keys = new PrivateKeyGenerator().Generate(210).ToList();
        MasterNodeCandidates = keys.Take(200).ToList();
        RandomKeys = keys.Skip(200).ToList();
        RandomSigner = new TestRandomSigner(MasterNodeCandidates);
        _useHotStuffModule = useHotStuffModule;
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
        Lazy<IVotesManager> votesManager,
        Lazy<ISigner> signer
    ) : FromContainer(stateReader, ethereumEcdsa, nonceManager, receiptStorage, txPool, worldStateManager, blockPreprocessorStep, blockTree, blockFinder, logFinder, chainHeadInfoProvider, dbProvider, specProvider, sealEngine, transactionComparerProvider, poSSwitcher, chainLevelInfoRepository, mainProcessingContext, readOnlyTxProcessingEnvFactory, blockProducerEnvFactory, configuration, testBlockchainUtil, poWTestBlockchainUtil, manualTimestamper, blockProductionTrigger, shareableTxProcessorSource, sealer, forkInfo)
    {
        public IEpochSwitchManager EpochSwitchManager => epochSwitchManager.Value;
        public IQuorumCertificateManager QuorumCertificateManager => quorumCertificateManager.Value;
        public ITimeoutCertificateManager TimeoutCertificateManager => timeoutCertificateManager.Value;
        public ISnapshotManager SnapshotManager => snapshotManager.Value;
        public ISigner Signer => signer.Value;
        public IVotesManager VotesManager => votesManager.Value;
    }
    // Please don't add any new parameter to this method. Pass any customization via autofac's configuration
    // or override method or a utility function that wrap around the autofac configuration.
    // Try to use plugin's module where possible to make sure prod and test components are wired similarly.
    protected override Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
    {
        JsonSerializer = new EthereumJsonSerializer();

        IConfigProvider configProvider = new ConfigProvider([.. CreateConfigs()]);

        ContainerBuilder builder = ConfigureContainer(new ContainerBuilder(), configProvider);
        ConfigureContainer(builder, configProvider);
        configurer?.Invoke(builder);

        Container = builder.Build();

        _fromXdcContainer = Container.Resolve<FromXdcContainer>();
        _fromContainer = (FromContainer)_fromXdcContainer;

        BlockchainProcessor.Start();

        BlockProducer = CreateTestBlockProducer();
        BlockProducerRunner ??= CreateBlockProducerRunner();
        BlockProducerRunner.Start();
        Suggester = new ProducedBlockSuggester(BlockTree, BlockProducerRunner);

        _cts = AutoCancelTokenSource.ThatCancelAfter(Debugger.IsAttached ? TimeSpan.FromMilliseconds(-1) : TimeSpan.FromMilliseconds(TestTimout));

        return Task.FromResult((TestBlockchain)this);
    }

    protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider)
    {
        var  container = base.ConfigureContainer(builder, configProvider)
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
            //.AddSingleton((_) => BlockProducerRunner)
            .AddSingleton<IBlockProducerRunner, XdcHotStuff>()
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(TestContext.CurrentContext.CancellationToken))

            .AddSingleton<TestBlockchainUtil.Config, Configuration>((cfg) => new TestBlockchainUtil.Config(cfg.SlotTime))

            .AddSingleton((ctx) => new PoWTestBlockchainUtil(
                ctx.Resolve<IBlockProducerRunner>(),
                ctx.Resolve<IManualBlockProductionTrigger>(),
                ctx.Resolve<ManualTimestamper>(),
                ctx.Resolve<IBlockTree>(),
                ctx.Resolve<ITxPool>(),
                ctx.Resolve<Configuration>().SlotTime
            ));

        return container;
    }

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
                MaxMasternodes = 30,
                CertThreshold = 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 2
            },
            new V2ConfigParams {
                SwitchRound = 10,
                MaxMasternodes = 30,
                CertThreshold = 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 2
            },
            new V2ConfigParams {
                SwitchRound = 15,
                MaxMasternodes = 30,
                CertThreshold = 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = 3000,
                MinePeriod = 2
            },
            new V2ConfigParams {
                SwitchRound = 20,
                MaxMasternodes = 30,
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

    protected override IBlockProducerRunner CreateBlockProducerRunner()
    {
        if (_useHotStuffModule)
        {
            return Container.Resolve<IBlockProducerRunner>();
        }
        return base.CreateBlockProducerRunner();
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

    public async Task AddBlocks(int count, bool withTransaction = false)
    {
        UInt256 nonce = 0;

        for (var i = 0; i < count; i++)
        {
            if (withTransaction)
                await AddBlock(CreateTransactionBuilder().WithNonce(nonce++).TestObject);
            else
                await AddBlock();
        }
    }

    public override async Task AddBlock(params Transaction[] transactions)
    {
        var b = await AddBlockWithoutCommitQc(transactions);
        CreateAndCommitQC((XdcBlockHeader)b.Header);
    }

    public override async Task AddBlockFromParent(BlockHeader parent, params Transaction[] transactions)
    {
        await base.AddBlockFromParent(parent, transactions);

        CheckIfTimeForSnapshot();

        CreateAndCommitQC((XdcBlockHeader)BlockTree.Head!.Header);
    }

    public async Task<Block> AddBlockWithoutCommitQc(params Transaction[] txs)
    {
        await base.AddBlock(txs);

        CheckIfTimeForSnapshot();

        return BlockTree.Head!;
    }

    private void CheckIfTimeForSnapshot()
    {
        var head = (XdcBlockHeader)BlockTree.Head!.Header;
        var headSpec = SpecProvider.GetXdcSpec(head, XdcContext.CurrentRound);
        if (ISnapshotManager.IsTimeforSnapshot(head.Number, headSpec))
        {
            SnapshotManager.StoreSnapshot(new Types.Snapshot(head.Number, head.Hash!, MasterNodeCandidates.Select(k => k.Address).ToArray()));
        }
    }

    public async Task TriggerAndSimulateBlockProposalAndVoting()
    {
        if (!_useHotStuffModule)
            throw new InvalidOperationException($"Can only be used when using the {nameof(XdcHotStuff)} module");
        var head = (XdcBlockHeader)BlockTree.Head!.Header;
        var spec = SpecProvider.GetXdcSpec(head, XdcContext.CurrentRound);
        var leader = ConsensusModule.GetLeaderAddress(head, XdcContext.CurrentRound, spec);

        EpochSwitchInfo epochSwitchInfo = EpochSwitchManager.GetEpochSwitchInfo(head)!;
        long epochSwitchNumber = epochSwitchInfo.EpochSwitchBlockInfo.BlockNumber;
        long gapNumber = epochSwitchNumber == 0 ? 0 : Math.Max(0, epochSwitchNumber - epochSwitchNumber % spec.EpochLength - spec.Gap);

        VoteDecoder voteDecoder = new VoteDecoder();

        var waitHandle = new TaskCompletionSource();
        BlockTree.NewHeadBlock += OnNewHead;
        try
        {
            //by setting the correct signer the block producer runner should trigger trying to propose a block
            Signer.SetSigner(MasterNodeCandidates.First(k => k.Address == leader));
            int count = 0;
            //Simulate voting until a new head is detected
            while (!waitHandle.Task.IsCompleted)
            {
                count++;
                if (count > 200)
                {
                    break;
                }
                //Will cast a random master candidate vote for the head block and when vote threshold is reached the block should be propose
                var vote = new Vote(new BlockRoundInfo(head.Hash!, XdcContext.CurrentRound, head.Number), (ulong)gapNumber);
                SignRandom(vote);
                var voteTask = this.VotesManager.HandleVote(vote);
            }
            //Blocks are not proposed 
            var finishedTask = await Task.WhenAny(waitHandle.Task, Task.Delay(10_000));
            if (finishedTask != waitHandle.Task)
                Assert.Fail("After 200 votes no new head could be detected. Something is wrong.");
        }
        finally
        {
            BlockTree.NewHeadBlock -= OnNewHead;
        }
        
        void OnNewHead(object? sender, BlockEventArgs e)
        {
            waitHandle.SetResult();
        }
        void SignRandom(Vote vote)
        {
            KeccakRlpStream stream = new();
            voteDecoder.Encode(stream, vote, RlpBehaviors.ForSealing);
            vote.Signature = RandomSigner.Sign(stream.GetValueHash());
            vote.Signer = RandomSigner.Address;
        }
    }

    public void CreateAndCommitQC(XdcBlockHeader header)
    {
        var headSpec = SpecProvider.GetXdcSpec(header, XdcContext.CurrentRound);
        EpochSwitchInfo switchInfo = EpochSwitchManager.GetEpochSwitchInfo(header.Hash!)!;

        var gap = (ulong)Math.Max(0, switchInfo.EpochSwitchBlockInfo.BlockNumber - switchInfo.EpochSwitchBlockInfo.BlockNumber % headSpec.EpochLength - headSpec.Gap);
        PrivateKey[] masterNodes = TakeRandomMasterNodes(headSpec, switchInfo);
        var headQc = XdcTestHelper.CreateQc(new BlockRoundInfo(header.Hash!, XdcContext.CurrentRound, header.Number), gap,
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
