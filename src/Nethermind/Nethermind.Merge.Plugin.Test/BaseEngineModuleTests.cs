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
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using Nethermind.History;
using Nethermind.Init.Modules;

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
        IMergeConfig? mergeConfig = null) =>
        new(mergeConfig);


    protected async Task<MergeTestBlockchain> CreateBlockchain(
        IReleaseSpec? releaseSpec = null,
        IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null,
        IExecutionRequestsProcessor? mockedExecutionRequestsProcessor = null,
        Action<ContainerBuilder>? configurer = null)
    {
        MergeTestBlockchain bc = CreateBaseBlockchain(mergeConfig);
        return await bc
            .BuildMergeTestBlockchain(configurer: (builder) =>
            {
                builder.AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(releaseSpec ?? London.Instance));

                if (mockedExecutionRequestsProcessor is not null) builder.AddScoped<IExecutionRequestsProcessor>(mockedExecutionRequestsProcessor);
                if (mockedPayloadService is not null) builder.AddSingleton<IPayloadPreparationService>(mockedPayloadService);

                configurer?.Invoke(builder);
            });
    }

    protected async Task<MergeTestBlockchain> CreateBlockchain(ISpecProvider specProvider)
        => await CreateBaseBlockchain().Build(specProvider);

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

            blocks.Add(getPayloadResult);
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

    protected static ExecutionPayload CreateParentBlockRequestOnHead(IBlockTree blockTree)
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
        public IMergeConfig MergeConfig { get; init; }
        public IPayloadPreparationService PayloadPreparationService => Container.Resolve<IPayloadPreparationService>();
        public StoringBlockImprovementContextFactory StoringBlockImprovementContextFactory => (StoringBlockImprovementContextFactory)BlockImprovementContextFactory;

        public Task WaitForImprovedBlock(Hash256? parentHash = null)
        {
            if (parentHash == null)
            {
                return StoringBlockImprovementContextFactory!.WaitForImprovedBlockWithCondition(_cts.Token, b => true);
            }
            return StoringBlockImprovementContextFactory!.WaitForImprovedBlockWithCondition(_cts.Token, b => b.Header.ParentHash == parentHash);
        }

        public IBeaconPivot BeaconPivot => Container.Resolve<IBeaconPivot>();

        public BeaconSync BeaconSync => Container.Resolve<BeaconSync>();

        public IWithdrawalProcessor WithdrawalProcessor => ((MainProcessingContext)MainProcessingContext).LifetimeScope.Resolve<IWithdrawalProcessor>();

        public ISyncPeerPool SyncPeerPool => Container.Resolve<ISyncPeerPool>();

        public Lazy<IEngineRpcModule> _lazyEngineRpcModule = null!;
        public IEngineRpcModule EngineRpcModule => _lazyEngineRpcModule.Value;

        public IHistoryPruner? HistoryPruner { get; set; }

        protected int _blockProcessingThrottle = 0;

        public MergeTestBlockchain ThrottleBlockProcessor(int delayMs)
        {
            _blockProcessingThrottle = delayMs;
            if (Container is not null && BranchProcessor is TestBranchProcessorInterceptor testBlockProcessor)
            {
                testBlockProcessor.DelayMs = delayMs;
            }
            return this;
        }

        public MergeTestBlockchain(IMergeConfig? mergeConfig = null)
        {
            MergeConfig = mergeConfig ?? new MergeConfig();
            if (MergeConfig.TerminalTotalDifficulty is null) MergeConfig.TerminalTotalDifficulty = "0";
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

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
                .AddScoped<IWithdrawalProcessor, WithdrawalProcessor>()
                .AddModule(new TestMergeModule(configProvider))
                .AddDecorator<IBranchProcessor>((ctx, branchProcessor) => new TestBranchProcessorInterceptor(branchProcessor, _blockProcessingThrottle))
                .AddDecorator<IBlockImprovementContextFactory>((ctx, factory) =>
                {
                    if (factory is StoringBlockImprovementContextFactory) return factory;
                    return new StoringBlockImprovementContextFactory(factory);
                })
                .AddSingleton<IBlockProducer>(ctx => this.BlockProducer)
                .AddSingleton<IPayloadPreparationService, IBlockProducer, ITxPool, IBlockImprovementContextFactory, ITimerFactory, ILogManager>(
                    (producer, txPool, ctxFactory, timer, logManager) =>
                        new PayloadPreparationService(
                            producer,
                            txPool,
                            ctxFactory,
                            timer,
                            logManager,
                            TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot),
                            50000)) // by default we want to avoid cleanup payload effects in testing                    )
                .AddSingleton<IEngineRequestsTracker>(Substitute.For<IEngineRequestsTracker>())
                .AddSingleton<ISyncPeerPool>(Substitute.For<ISyncPeerPool>())
                .AddSingleton<ISyncPointers>(Substitute.For<ISyncPointers>())
                .AddSingleton<ISyncProgressResolver>(Substitute.For<ISyncProgressResolver>())
                .AddSingleton<ISyncModeSelector>(new StaticSelector(SyncMode.All))
                .AddSingleton<IPeerRefresher>(Substitute.For<IPeerRefresher>())
                .WithGenesisPostProcessor((block, worldState) =>
                {
                    // GenesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.Genesis.WithTimestamp(1UL);
                    block.Header.Timestamp = 1UL;
                })
                .Intercept<IInitConfig>((initConfig) => initConfig.DisableGcOnNewPayload = false);

        protected override IBlockProducer CreateTestBlockProducer()
        {
            IBlockProducer preMergeBlockProducer = base.CreateTestBlockProducer();
            BlocksConfig blocksConfig = new() { MinGasPrice = 0 };
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, blocksConfig);
            PostMergeBlockProducerFactory? blockProducerFactory = new(
                SpecProvider,
                SealEngine,
                Timestamper,
                blocksConfig,
                LogManager,
                targetAdjustedGasLimitCalculator);

            IBlockProducerEnv blockProducerEnv = BlockProducerEnvFactory.Create();
            PostMergeBlockProducer? postMergeBlockProducer = blockProducerFactory.Create(blockProducerEnv);
            BlockProducer = postMergeBlockProducer;

            return new MergeBlockProducer(preMergeBlockProducer, postMergeBlockProducer, PoSSwitcher);
        }

        protected override async Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
        {
            TestBlockchain bc = await base.Build(configurer);
            BeaconSync.AllowBeaconHeaderSync();
            _lazyEngineRpcModule = bc.Container.Resolve<Lazy<IEngineRpcModule>>();
            return bc;
        }

        public IManualBlockFinalizationManager BlockFinalizationManager => Container.Resolve<IManualBlockFinalizationManager>();

        public IBlockImprovementContextFactory BlockImprovementContextFactory =>
            Container.Resolve<IBlockImprovementContextFactory>();

        public async Task<MergeTestBlockchain> Build(ISpecProvider specProvider) =>
            (MergeTestBlockchain)await Build(configurer: (builder) => builder.AddSingleton(specProvider));

        public async Task<MergeTestBlockchain> BuildMergeTestBlockchain(Action<ContainerBuilder> configurer) =>
            (MergeTestBlockchain)await Build(configurer: configurer);
    }
}
