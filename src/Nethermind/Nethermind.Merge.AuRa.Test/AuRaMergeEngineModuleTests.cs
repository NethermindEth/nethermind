// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Timers;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeEngineModuleTests : EngineModuleTests
{
    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null,
        IPayloadPreparationService? mockedPayloadService = null,
        ILogManager? logManager = null)
        => new MergeAuRaTestBlockchain(mergeConfig, mockedPayloadService, null, logManager);

    protected override Hash256 ExpectedBlockHash => new("0x990d377b67dbffee4a60db6f189ae479ffb406e8abea16af55e0469b8524cf46");

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public override Task forkchoiceUpdatedV2_should_validate_withdrawals((IReleaseSpec Spec,
        string ErrorMessage,
        Withdrawal[]? Withdrawals,
        string BlockHash,
        int ErrorCode
        ) input)
        => base.forkchoiceUpdatedV2_should_validate_withdrawals(input);

    [TestCase(
        "0x1f26afbef938a122f4f55d2f081ac81cd9c8851ca22452fa5baf58845e574fc6",
        "0x343ab3716f2475c9cdd993dc654dd0ea143379a62f0556180bff1869eb451858",
        "0x26b9598dd31cd520c6dcaf4f6fa13e279b4fa1f94d150357290df0e944f53115",
        "0x2de3ad8b5939b3b9")]
    public override Task Should_process_block_as_expected_V4(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V4(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0xca2fbb93848df6500fcc33f9036f43f33db9844719f0a5fc69079d8d90dbb28f",
        "0xc6caeb09b3f26ddda9b1adb956fadbe29d7d90cff9bf2e2b0f3f1d0ec9296a72",
        "0xd4ab6af74f5566d54b164115a9b00726bd35e2170d206e466c4be30ebfe23894",
        "0x103ea062e6e09c06")]
    public override Task Should_process_block_as_expected_V2(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V2(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0xe4333fcde906675e50500bf53a6c73bc51b2517509bc3cff2d24d0de9b8dd23e",
        "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926",
        "0xb22228e153345f9b")]
    public override Task processing_block_should_serialize_valid_responses(string blockHash, string latestValidHash, string payloadId)
        => base.processing_block_should_serialize_valid_responses(blockHash, latestValidHash, payloadId);

    [Test]
    [TestCase(
        "0xa66ec67b117f57388da53271f00c22a68e6c297b564f67c5904e6f2662881875",
        "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926"
        )]
    [Parallelizable(ParallelScope.None)]
    [Obsolete]
    public override Task forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http(string blockHash, string parentHash)
        => base.forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http(blockHash, parentHash);

    [Ignore("Withdrawals are not withdrawan due to lack of Aura contract in tests")]
    public override Task Can_apply_withdrawals_correctly((Withdrawal[][] Withdrawals, (Address Account, UInt256 BalanceIncrease)[] ExpectedAccountIncrease) input)
    {
        return base.Can_apply_withdrawals_correctly(input);
    }

    public class MergeAuRaTestBlockchain : MergeTestBlockchain
    {
        private AuRaNethermindApi? _api;
        protected ITxSource? _additionalTxSource;
        private AuRaMergeBlockProducerEnvFactory _blockProducerEnvFactory = null!;

        public MergeAuRaTestBlockchain(IMergeConfig? mergeConfig = null, IPayloadPreparationService? mockedPayloadPreparationService = null, ITxSource? additionalTxSource = null, ILogManager? logManager = null)
            : base(mergeConfig, mockedPayloadPreparationService, logManager)
        {
            SealEngineType = Core.SealEngineType.AuRa;
            _additionalTxSource = additionalTxSource;
        }

        protected override Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null) =>
            base.Build(builder =>
            {
                builder
                    .AddDecorator<ISpecProvider>((ctx, specProvider) =>
                    {
                        // I guess ideally, just make a wrapper for `ISpecProvider` that replace only SealEngine.
                        ISpecProvider unwrappedSpecProvider = specProvider;
                        while (unwrappedSpecProvider is OverridableSpecProvider overridableSpecProvider)
                            unwrappedSpecProvider = overridableSpecProvider.SpecProvider;
                        if (unwrappedSpecProvider is TestSingleReleaseSpecProvider provider)
                            provider.SealEngine = SealEngineType;
                        return specProvider;
                    })

                    // Aura uses `AuRaNethermindApi` for initialization, so need to do some additional things here
                    // as normally, test blockchain don't use INethermindApi at all. Note: This test does not
                    // seems to use aura block processor which means a lot of aura things is not available here.
                    .AddModule(new AuraModule(ChainSpec))
                    .AddSingleton<NethermindApi.Dependencies>()
                    .AddSingleton<IReportingValidator>(NullReportingValidator.Instance)

                    .AddSingleton<IBlockProducerEnvFactory>((ctx) => _blockProducerEnvFactory)
                    .AddDecorator<AuRaNethermindApi>((ctx, api) =>
                    {
                        // Yes getting from `TestBlockchain` itself, since steps are not run
                        // and some of these are not from DI. you know... chicken and egg, but dont forgot about rooster.
                        api.DbProvider = DbProvider;
                        api.TxPool = TxPool;
                        api.TransactionComparerProvider = TransactionComparerProvider;
                        api.FinalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
                        return api;
                    });
                configurer?.Invoke(builder);
            });

        protected override ChainSpec CreateChainSpec()
        {
            ChainSpec baseChainSpec = base.CreateChainSpec();
            baseChainSpec.EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new AuRaChainSpecEngineParameters
                {
                    WithdrawalContractAddress = new("0xbabe2bed00000000000000000000000000000003")
                });
            baseChainSpec.Parameters = new ChainParameters();
            return baseChainSpec;
        }

        protected override IBlockProcessor CreateBlockProcessor(IWorldState state)
        {
            _api = Container.Resolve<AuRaNethermindApi>();

            WithdrawalContractFactory withdrawalContractFactory = new(Container.Resolve<ChainSpec>().EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>(), Container.Resolve<IAbiEncoder>());
            WithdrawalProcessor = new AuraWithdrawalProcessor(
                withdrawalContractFactory.Create(TxProcessor),
                LogManager
            );

            IBlockProcessor processor = new BlockProcessor(
                SpecProvider,
                BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, state),
                state,
                ReceiptStorage,
                new BeaconBlockRootHandler(TxProcessor, state),
                new BlockhashStore(SpecProvider, state),
                LogManager,
                WithdrawalProcessor,
                ExecutionRequestsProcessorOverride ?? new ExecutionRequestsProcessor(TxProcessor),
                CreateBlockCachePreWarmer());

            return new TestBlockProcessorInterceptor(processor, _blockProcessingThrottle);
        }


        protected override IBlockProducer CreateTestBlockProducer(ITxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
        {
            BlocksConfig blocksConfig = new() { MinGasPrice = 0 };
            ISyncConfig syncConfig = new SyncConfig();
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, blocksConfig);
            EthSyncingInfo = new EthSyncingInfo(BlockTree, Substitute.For<ISyncPointers>(), syncConfig,
                new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), LogManager);
            PostMergeBlockProducerFactory blockProducerFactory = new(
                SpecProvider,
                SealEngine,
                Timestamper,
                blocksConfig,
                LogManager,
                targetAdjustedGasLimitCalculator);

            AuRaMergeBlockProducerEnvFactory blockProducerEnvFactory = new(
                _api!.ChainSpec,
                _api.AbiEncoder,
                _api.CreateStartBlockProducer,
                _api.ReadOnlyTxProcessingEnvFactory,
                WorldStateManager,
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
            this._blockProducerEnvFactory = blockProducerEnvFactory;

            BlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create(_additionalTxSource);
            PostMergeBlockProducer postMergeBlockProducer = blockProducerFactory.Create(blockProducerEnv);
            PostMergeBlockProducer = postMergeBlockProducer;
            PayloadPreparationService ??= new PayloadPreparationService(
                postMergeBlockProducer,
                StoringBlockImprovementContextFactory = new StoringBlockImprovementContextFactory(CreateBlockImprovementContextFactory(PostMergeBlockProducer)),
                TimerFactory.Default,
                LogManager,
                TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot),
                50000 // by default we want to avoid cleanup payload effects in testing
            );

            IAuRaStepCalculator auraStepCalculator = Substitute.For<IAuRaStepCalculator>();
            auraStepCalculator.TimeToNextStep.Returns(TimeSpan.FromMilliseconds(0));
            var env = blockProducerEnvFactory.Create();
            FollowOtherMiners gasLimitCalculator = new(MainnetSpecProvider.Instance);
            AuRaBlockProducer preMergeBlockProducer = new(
                txPoolTxSource,
                env.ChainProcessor,
                env.ReadOnlyStateProvider,
                sealer,
                BlockTree,
                Timestamper,
                auraStepCalculator,
                NullReportingValidator.Instance,
                new AuRaConfig(),
                gasLimitCalculator,
                SpecProvider,
                LogManager,
                blocksConfig
            );

            return new MergeBlockProducer(preMergeBlockProducer, postMergeBlockProducer, PoSSwitcher);
        }

        protected virtual IBlockImprovementContextFactory CreateBlockImprovementContextFactory(IBlockProducer blockProducer)
            => new BlockImprovementContextFactory(blockProducer, TimeSpan.FromSeconds(MergeConfig.SecondsPerSlot));
    }
}
