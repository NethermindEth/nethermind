// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.HealthChecks;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public abstract partial class BaseEngineModuleTests
{
    [SetUp]
    public Task Setup()
    {
        ThreadPool.GetMaxThreads(out int worker, out int completion);
        ThreadPool.SetMinThreads(worker, completion);
        return KzgPolynomialCommitments.InitializeAsync();
    }

    protected virtual MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null,
        ILogManager? logManager = null) =>
        new(mergeConfig, mockedPayloadService, logManager);


    protected async Task<MergeTestBlockchain> CreateBlockchain(
        IReleaseSpec? releaseSpec = null,
        IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null,
        ILogManager? logManager = null,
        IExecutionRequestsProcessor? mockedExecutionRequestsProcessor = null,
        Action<ContainerBuilder>? configurer = null)
    {
        var bc = CreateBaseBlockchain(mergeConfig, mockedPayloadService, logManager);
        bc.ExecutionRequestsProcessorOverride = mockedExecutionRequestsProcessor;
        return await bc
            .BuildMergeTestBlockchain(configurer: (builder) =>
            {
                builder.AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(releaseSpec ?? London.Instance));

                if (mockedExecutionRequestsProcessor is not null) builder.AddScoped<IExecutionRequestsProcessor>(mockedExecutionRequestsProcessor);

                configurer?.Invoke(builder);
            });
    }

    protected async Task<MergeTestBlockchain> CreateBlockchain(ISpecProvider specProvider,
        ILogManager? logManager = null)
        => await CreateBaseBlockchain(logManager: logManager).Build(specProvider);

    protected IEngineRpcModule CreateEngineModule(MergeTestBlockchain chain, ISyncConfig? syncConfig = null, TimeSpan? newPayloadTimeout = null, int newPayloadCacheSize = 50)
    {
        IPeerRefresher peerRefresher = Substitute.For<IPeerRefresher>();
        var synchronizationConfig = syncConfig ?? new SyncConfig();

        chain.BlockTree.SyncPivot = (
            LongConverter.FromString(synchronizationConfig.PivotNumber),
            synchronizationConfig.PivotHash is null ? Keccak.Zero : new Hash256(Bytes.FromHexString(synchronizationConfig.PivotHash))
        );
        chain.BeaconPivot = new BeaconPivot(synchronizationConfig, new MemDb(), chain.BlockTree, chain.PoSSwitcher, chain.LogManager);
        BlockCacheService blockCacheService = new();
        InvalidChainTracker.InvalidChainTracker invalidChainTracker = new(
            chain.PoSSwitcher,
            chain.BlockTree,
            blockCacheService,
            chain.LogManager);
        invalidChainTracker.SetupBlockchainProcessorInterceptor(chain.BlockchainProcessor);
        chain.BeaconSync = new BeaconSync(chain.BeaconPivot, chain.BlockTree, synchronizationConfig, blockCacheService, chain.PoSSwitcher, chain.LogManager);
        chain.BeaconSync.AllowBeaconHeaderSync();
        EngineRpcCapabilitiesProvider capabilitiesProvider = new(chain.SpecProvider);
        return new EngineRpcModule(
            new GetPayloadV1Handler(
                chain.PayloadPreparationService!,
                chain.SpecProvider!,
                chain.LogManager),
            new GetPayloadV2Handler(
                chain.PayloadPreparationService!,
                chain.SpecProvider!,
                chain.LogManager),
            new GetPayloadV3Handler(
                chain.PayloadPreparationService!,
                chain.SpecProvider!,
                chain.LogManager),
            new GetPayloadV4Handler(
                chain.PayloadPreparationService!,
                chain.SpecProvider!,
                chain.LogManager),
            new GetPayloadV5Handler(
                chain.PayloadPreparationService!,
                chain.SpecProvider!,
                chain.LogManager),
            new NewPayloadHandler(
                chain.BlockValidator,
                chain.BlockTree,
                chain.PoSSwitcher,
                chain.BeaconSync,
                chain.BeaconPivot,
                blockCacheService,
                chain.BlockProcessingQueue,
                invalidChainTracker,
                chain.BeaconSync,
                chain.LogManager,
                newPayloadTimeout,
                storeReceipts: true,
                newPayloadCacheSize),
            new ForkchoiceUpdatedHandler(
                chain.BlockTree,
                chain.BlockFinalizationManager,
                chain.PoSSwitcher,
                chain.PayloadPreparationService!,
                chain.BlockProcessingQueue,
                blockCacheService,
                invalidChainTracker,
                chain.BeaconSync,
                chain.BeaconPivot,
                peerRefresher,
                chain.SpecProvider,
                chain.SyncPeerPool,
                chain.LogManager),
            new GetPayloadBodiesByHashV1Handler(chain.BlockTree, chain.LogManager),
            new GetPayloadBodiesByRangeV1Handler(chain.BlockTree, chain.LogManager),
            new ExchangeTransitionConfigurationV1Handler(chain.PoSSwitcher, chain.LogManager),
            new ExchangeCapabilitiesHandler(capabilitiesProvider, chain.LogManager),
            new GetBlobsHandler(chain.TxPool),
            new GetBlobsHandlerV2(chain.TxPool),
            Substitute.For<IEngineRequestsTracker>(),
            chain.SpecProvider,
            new GCKeeper(NoGCStrategy.Instance, chain.LogManager),
            chain.LogManager);
    }

    protected async Task<IReadOnlyList<ExecutionPayload>> ProduceBranchV1(IEngineRpcModule rpc,
        MergeTestBlockchain chain,
        int count, ExecutionPayload startingParentBlock, bool setHead, Hash256? random = null,
        ulong slotLength = 12)
    {
        List<ExecutionPayload> blocks = new();
        ExecutionPayload parentBlock = startingParentBlock;
        Block? block = parentBlock.TryGetBlock().Block;
        UInt256? startingTotalDifficulty = block!.IsGenesis
            ? block.Difficulty : chain.BlockFinder.FindHeader(block!.Header!.ParentHash!)!.TotalDifficulty;
        BlockHeader parentHeader = block!.Header;
        parentHeader.TotalDifficulty = startingTotalDifficulty +
                                       parentHeader.Difficulty;
        for (int i = 0; i < count; i++)
        {
            ExecutionPayload? getPayloadResult = await BuildAndGetPayloadOnBranch(rpc, chain, parentHeader,
                parentBlock.Timestamp + slotLength,
                random ?? TestItem.KeccakA, Address.Zero);
            PayloadStatusV1 payloadStatusResponse = (await rpc.engine_newPayloadV1(getPayloadResult)).Data;
            payloadStatusResponse.Status.Should().Be(PayloadStatus.Valid);
            if (setHead)
            {
                Hash256 newHead = getPayloadResult!.BlockHash;
                ForkchoiceStateV1 forkchoiceStateV1 = new(newHead, newHead, newHead);
                ResultWrapper<ForkchoiceUpdatedV1Result> setHeadResponse = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
                setHeadResponse.Data.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);
                setHeadResponse.Data.PayloadId.Should().Be(null);
            }

            blocks.Add((getPayloadResult));
            parentBlock = getPayloadResult;
            block = parentBlock.TryGetBlock().Block!;
            block.Header.TotalDifficulty = parentHeader.TotalDifficulty + block.Header.Difficulty;
            parentHeader = block.Header;
        }

        return blocks;
    }

    protected async Task<ExecutionPayload> BuildAndGetPayloadOnBranch(
        IEngineRpcModule rpc, MergeTestBlockchain chain, BlockHeader parentHeader,
        ulong timestamp, Hash256 random, Address feeRecipient)
    {
        PayloadAttributes payloadAttributes =
            new() { Timestamp = timestamp, PrevRandao = random, SuggestedFeeRecipient = feeRecipient };

        // we're using payloadService directly, because we can't use fcU for branch
        string payloadId = chain.PayloadPreparationService!.StartPreparingPayload(parentHeader, payloadAttributes)!;

        ResultWrapper<ExecutionPayload?> getPayloadResult =
            await rpc.engine_getPayloadV1(Bytes.FromHexString(payloadId));
        return getPayloadResult.Data!;
    }

    protected ExecutionPayload CreateParentBlockRequestOnHead(IBlockTree blockTree)
    {
        Block? head = blockTree.Head ?? throw new NotSupportedException();
        return new ExecutionPayload()
        {
            BlockNumber = head.Number,
            BlockHash = head.Hash!,
            StateRoot = head.StateRoot!,
            ReceiptsRoot = head.ReceiptsRoot!,
            GasLimit = head.GasLimit,
            Timestamp = head.Timestamp,
            BaseFeePerGas = head.BaseFeePerGas,
        };
    }

    public class MergeTestBlockchain : TestBlockchain
    {
        public IMergeConfig MergeConfig { get; set; }

        public PostMergeBlockProducer? PostMergeBlockProducer { get; set; }

        public IPayloadPreparationService? PayloadPreparationService { get; set; }
        public StoringBlockImprovementContextFactory? StoringBlockImprovementContextFactory { get; set; }

        public Task WaitForImprovedBlock(Hash256? parentHash = null)
        {
            if (parentHash == null)
            {
                return StoringBlockImprovementContextFactory!.WaitForImprovedBlockWithCondition(_cts.Token, b => true);
            }
            return StoringBlockImprovementContextFactory!.WaitForImprovedBlockWithCondition(_cts.Token, b => b.Header.ParentHash == parentHash);
        }

        public Task WaitForImprovedBlockWithCondition(Func<Block, bool> predicate)
        {
            return StoringBlockImprovementContextFactory!.WaitForImprovedBlockWithCondition(_cts.Token, predicate);
        }

        public ISealValidator? SealValidator => Container.Resolve<ISealValidator>();

        public IBeaconPivot? BeaconPivot { get; set; }

        public BeaconSync? BeaconSync { get; set; }

        public IWithdrawalProcessor? WithdrawalProcessor { get; set; }

        public ISyncPeerPool SyncPeerPool { get; set; }

        public IExecutionRequestsProcessor? ExecutionRequestsProcessorOverride { get; set; }

        protected int _blockProcessingThrottle = 0;

        public MergeTestBlockchain ThrottleBlockProcessor(int delayMs)
        {
            _blockProcessingThrottle = delayMs;
            if (BlockProcessor is TestBlockProcessorInterceptor testBlockProcessor)
            {
                testBlockProcessor.DelayMs = delayMs;
            }
            return this;
        }

        public MergeTestBlockchain(IMergeConfig? mergeConfig = null, IPayloadPreparationService? mockedPayloadPreparationService = null, ILogManager? logManager = null)
        {
            GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis.WithTimestamp(1UL);
            MergeConfig = mergeConfig ?? new MergeConfig() { TerminalTotalDifficulty = "0" };
            PayloadPreparationService = mockedPayloadPreparationService;
            SyncPeerPool = Substitute.For<ISyncPeerPool>();
            LogManager = logManager ?? LogManager;
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

        public sealed override ILogManager LogManager { get; set; } = LimboLogs.Instance;

        public IEthSyncingInfo? EthSyncingInfo { get; protected set; }

        protected override ChainSpec CreateChainSpec()
        {
            return new ChainSpec() { Genesis = Core.Test.Builders.Build.A.Block.WithDifficulty(0).TestObject };
        }

        protected override IEnumerable<IConfig> CreateConfigs()
        {
            return base.CreateConfigs().Concat([MergeConfig, SyncConfig.Default]);
        }

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddModule(new MergeModule(configProvider));

        protected override IBlockProducer CreateTestBlockProducer(ITxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
        {
            IBlockProducer preMergeBlockProducer =
                base.CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
            BlocksConfig blocksConfig = new() { MinGasPrice = 0 };
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, blocksConfig);
            ISyncConfig syncConfig = new SyncConfig();
            EthSyncingInfo = new EthSyncingInfo(BlockTree, Substitute.For<ISyncPointers>(), syncConfig,
                new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), LogManager);
            PostMergeBlockProducerFactory? blockProducerFactory = new(
                SpecProvider,
                SealEngine,
                Timestamper,
                blocksConfig,
                LogManager,
                targetAdjustedGasLimitCalculator);

            BlockProducerEnvFactory blockProducerEnvFactory = new(
                WorldStateManager!,
                ReadOnlyTxProcessingEnvFactory,
                BlockTree,
                SpecProvider,
                BlockValidator,
                NoBlockRewards.Instance,
                ReceiptStorage,
                BlockPreprocessorStep,
                TxPool,
                transactionComparerProvider,
                blocksConfig,
                LogManager);
            blockProducerEnvFactory.ExecutionRequestsProcessorOverride = ExecutionRequestsProcessorOverride;

            BlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create();
            PostMergeBlockProducer? postMergeBlockProducer = blockProducerFactory.Create(blockProducerEnv);
            PostMergeBlockProducer = postMergeBlockProducer;
            BlockImprovementContextFactory ??= new BlockImprovementContextFactory(PostMergeBlockProducer, TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot));
            PayloadPreparationService ??= new PayloadPreparationService(
                postMergeBlockProducer,
                BlockImprovementContextFactory,
                TimerFactory.Default,
                LogManager,
                TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot),
                50000); // by default we want to avoid cleanup payload effects in testing

            return new MergeBlockProducer(preMergeBlockProducer, postMergeBlockProducer, PoSSwitcher);
        }

        protected override IBlockProcessor CreateBlockProcessor(IWorldState worldState)
        {
            WithdrawalProcessor = new WithdrawalProcessor(worldState, LogManager);

            IBlockProcessor processor = new BlockProcessor(
                SpecProvider,
                BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, worldState),
                worldState,
                ReceiptStorage,
                new BeaconBlockRootHandler(TxProcessor, worldState),
                new BlockhashStore(SpecProvider, worldState),
                LogManager,
                WithdrawalProcessor,
                ExecutionRequestsProcessorOverride ?? MainExecutionRequestsProcessor,
                CreateBlockCachePreWarmer());

            return new TestBlockProcessorInterceptor(processor, _blockProcessingThrottle);
        }

        public IManualBlockFinalizationManager BlockFinalizationManager { get; } = new ManualBlockFinalizationManager();

        public IBlockImprovementContextFactory BlockImprovementContextFactory
        {
            get => StoringBlockImprovementContextFactory!;
            set
            {
                StoringBlockImprovementContextFactory = value as StoringBlockImprovementContextFactory ?? new StoringBlockImprovementContextFactory(value);
            }
        }

        public async Task<MergeTestBlockchain> Build(ISpecProvider specProvider) =>
            (MergeTestBlockchain)await Build(configurer: (builder) => builder.AddSingleton<ISpecProvider>(specProvider));

        public async Task<MergeTestBlockchain> BuildMergeTestBlockchain(Action<ContainerBuilder> configurer) =>
            (MergeTestBlockchain)await Build(configurer: configurer);
    }
}
