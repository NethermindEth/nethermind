// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Eth;
using Nethermind.HealthChecks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [SetUp]
    public Task Setup()
    {
        return KzgPolynomialCommitments.InitializeAsync();
    }

    protected virtual MergeTestBlockchain CreateBaseBlockChain(IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null, ILogManager? logManager = null) =>
        new(mergeConfig, mockedPayloadService, logManager);

    protected async Task<MergeTestBlockchain> CreateBlockChain(IReleaseSpec? releaseSpec, IMergeConfig? mergeConfig = null)
        => await CreateBlockChain(mergeConfig, null, releaseSpec);


    protected async Task<MergeTestBlockchain> CreateBlockChain(IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null, IReleaseSpec? releaseSpec = null)
        => await CreateBaseBlockChain(mergeConfig, mockedPayloadService)
            .Build(new TestSingleReleaseSpecProvider(releaseSpec ?? London.Instance));

    protected async Task<MergeTestBlockchain> CreateBlockChain(ISpecProvider specProvider,
        ILogManager? logManager = null)
        => await CreateBaseBlockChain(null, null, logManager).Build(specProvider);

    private IEngineRpcModule CreateEngineModule(MergeTestBlockchain chain, ISyncConfig? syncConfig = null, TimeSpan? newPayloadTimeout = null, int newPayloadCacheSize = 50)
    {
        IPeerRefresher peerRefresher = Substitute.For<IPeerRefresher>();
        var synchronizationConfig = syncConfig ?? new SyncConfig();

        chain.BeaconPivot = new BeaconPivot(synchronizationConfig, new MemDb(), chain.BlockTree, chain.LogManager);
        BlockCacheService blockCacheService = new();
        InvalidChainTracker.InvalidChainTracker invalidChainTracker = new(
            chain.PoSSwitcher,
            chain.BlockTree,
            blockCacheService,
            chain.LogManager);
        invalidChainTracker.SetupBlockchainProcessorInterceptor(chain.BlockchainProcessor);
        chain.BeaconSync = new BeaconSync(chain.BeaconPivot, chain.BlockTree, synchronizationConfig, blockCacheService, chain.LogManager);
        EngineRpcCapabilitiesProvider capabilitiesProvider = new(chain.SpecProvider);
        return new EngineRpcModule(
            new GetPayloadV1Handler(
                chain.PayloadPreparationService!,
                chain.LogManager),
            new GetPayloadV2Handler(
                chain.PayloadPreparationService!,
                chain.LogManager),
            new GetPayloadV3Handler(
                chain.PayloadPreparationService!,
                chain.LogManager),
            new NewPayloadHandler(
                chain.BlockValidator,
                chain.BlockTree,
                new InitConfig(),
                synchronizationConfig,
                chain.PoSSwitcher,
                chain.BeaconSync,
                chain.BeaconPivot,
                blockCacheService,
                chain.BlockProcessingQueue,
                invalidChainTracker,
                chain.BeaconSync,
                chain.LogManager,
                newPayloadTimeout,
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
                chain.LogManager),
            new GetPayloadBodiesByHashV1Handler(chain.BlockTree, chain.LogManager),
            new GetPayloadBodiesByRangeV1Handler(chain.BlockTree, chain.LogManager),
            new ExchangeTransitionConfigurationV1Handler(chain.PoSSwitcher, chain.LogManager),
            new ExchangeCapabilitiesHandler(capabilitiesProvider, chain.LogManager),
            chain.SpecProvider,
            new GCKeeper(NoGCStrategy.Instance, chain.LogManager),
            chain.LogManager);
    }

    public class MergeTestBlockchain : TestBlockchain
    {
        public IMergeConfig MergeConfig { get; set; }

        public PostMergeBlockProducer? PostMergeBlockProducer { get; set; }

        public IPayloadPreparationService? PayloadPreparationService { get; set; }

        public ISealValidator? SealValidator { get; set; }

        public IBeaconPivot? BeaconPivot { get; set; }

        public BeaconSync? BeaconSync { get; set; }

        private int _blockProcessingThrottle = 0;

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
            LogManager = logManager ?? LogManager;
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

        public sealed override ILogManager LogManager { get; set; } = LimboLogs.Instance;

        public IEthSyncingInfo? EthSyncingInfo { get; protected set; }

        protected override IBlockProducer CreateTestBlockProducer(TxPoolTxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
        {
            SealEngine = new MergeSealEngine(SealEngine, PoSSwitcher, SealValidator!, LogManager);
            IBlockProducer preMergeBlockProducer =
                base.CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
            BlocksConfig blocksConfig = new() { MinGasPrice = 0 };
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, blocksConfig);
            ISyncConfig syncConfig = new SyncConfig();
            EthSyncingInfo = new EthSyncingInfo(BlockTree, ReceiptStorage, syncConfig, new StaticSelector(SyncMode.All), LogManager);
            PostMergeBlockProducerFactory? blockProducerFactory = new(
                SpecProvider,
                SealEngine,
                Timestamper,
                blocksConfig,
                LogManager,
                targetAdjustedGasLimitCalculator);

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


            BlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create();
            PostMergeBlockProducer? postMergeBlockProducer = blockProducerFactory.Create(
                blockProducerEnv, BlockProductionTrigger);
            PostMergeBlockProducer = postMergeBlockProducer;
            PayloadPreparationService ??= new PayloadPreparationService(
                postMergeBlockProducer,
                new BlockImprovementContextFactory(BlockProductionTrigger, TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot)),
                TimerFactory.Default,
                LogManager,
                TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot),
                50000); // by default we want to avoid cleanup payload effects in testing
            return new MergeBlockProducer(preMergeBlockProducer, postMergeBlockProducer, PoSSwitcher);
        }

        protected override IBlockProcessor CreateBlockProcessor()
        {
            BlockValidator = CreateBlockValidator();
            IBlockProcessor processor = new BlockProcessor(
                SpecProvider,
                BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                State,
                ReceiptStorage,
                NullWitnessCollector.Instance,
                LogManager);

            return new TestBlockProcessorInterceptor(processor, _blockProcessingThrottle);
        }

        private IBlockValidator CreateBlockValidator()
        {
            IBlockCacheService blockCacheService = new BlockCacheService();
            PoSSwitcher = new PoSSwitcher(MergeConfig, SyncConfig.Default, new MemDb(), BlockTree, SpecProvider, LogManager);
            SealValidator = new MergeSealValidator(PoSSwitcher, Always.Valid);
            HeaderValidator preMergeHeaderValidator = new HeaderValidator(BlockTree, SealValidator, SpecProvider, LogManager);
            HeaderValidator = new MergeHeaderValidator(PoSSwitcher, preMergeHeaderValidator, BlockTree, SpecProvider, SealValidator, LogManager);

            return new BlockValidator(
                new TxValidator(SpecProvider.ChainId),
                HeaderValidator,
                Always.Valid,
                SpecProvider,
                LogManager);
        }

        public IManualBlockFinalizationManager BlockFinalizationManager { get; } = new ManualBlockFinalizationManager();

        protected override async Task<TestBlockchain> Build(ISpecProvider? specProvider = null, UInt256? initialValues = null)
        {
            TestBlockchain chain = await base.Build(specProvider, initialValues);
            return chain;
        }

        public async Task<MergeTestBlockchain> Build(ISpecProvider? specProvider = null) =>
            (MergeTestBlockchain)await Build(specProvider, null);
    }
}

internal class TestBlockProcessorInterceptor : IBlockProcessor
{
    private readonly IBlockProcessor _blockProcessorImplementation;
    public int DelayMs { get; set; }
    public Exception? ExceptionToThrow { get; set; }

    public TestBlockProcessorInterceptor(IBlockProcessor baseBlockProcessor, int delayMs)
    {
        _blockProcessorImplementation = baseBlockProcessor;
        DelayMs = delayMs;
    }

    public Block[] Process(Keccak newBranchStateRoot, List<Block> suggestedBlocks, ProcessingOptions processingOptions,
        IBlockTracer blockTracer)
    {
        if (DelayMs > 0)
        {
            Thread.Sleep(DelayMs);
        }

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return _blockProcessorImplementation.Process(newBranchStateRoot, suggestedBlocks, processingOptions, blockTracer);
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing
    {
        add => _blockProcessorImplementation.BlocksProcessing += value;
        remove => _blockProcessorImplementation.BlocksProcessing -= value;
    }

    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed
    {
        add => _blockProcessorImplementation.BlockProcessed += value;
        remove => _blockProcessorImplementation.BlockProcessed -= value;
    }

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed
    {
        add => _blockProcessorImplementation.TransactionProcessed += value;
        remove => _blockProcessorImplementation.TransactionProcessed -= value;
    }
}
