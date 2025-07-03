// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Merge.AuRa.Contracts;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.Evm.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeEngineModuleTests : EngineModuleTests
{
    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null)
        => new MergeAuRaTestBlockchain(mergeConfig);

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
        "0xd6ac1db7fee77f895121329b5949ddfb5258c952868a3707bb1104d8f219df2e",
        "0x912ef8152bf54f81762e26d2a4f0793fb2a0a55b7ce5a9ff7f6879df6d94a6b3",
        "0x26b9598dd31cd520c6dcaf4f6fa13e279b4fa1f94d150357290df0e944f53115",
        "0x0117c925cabe3c1d")]
    public override Task Should_process_block_as_expected_V4(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V4(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0xca2fbb93848df6500fcc33f9036f43f33db9844719f0a5fc69079d8d90dbb28f",
        "0x4b8e5a6567229461665f1475a39665a3df55b367ca5fd9cc861fe70d4d5836c3",
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

        public MergeAuRaTestBlockchain(IMergeConfig? mergeConfig = null)
            : base(mergeConfig)
        {
            SealEngineType = Core.SealEngineType.AuRa;
        }

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider)
        {
            return base.ConfigureContainer(builder, configProvider)
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
                .AddModule(new AuRaModule(ChainSpec))
                .AddModule(new AuRaMergeModule())
                .AddSingleton<NethermindApi.Dependencies>()
                .AddSingleton<IReportingValidator>(NullReportingValidator.Instance)
                .AddSingleton<ISealer>(NullSealEngine.Instance) // Test not originally made with aura sealer

                .AddScoped<WithdrawalContractFactory>()
                .AddScoped<IWithdrawalContract, WithdrawalContractFactory, ITransactionProcessor>((factory, txProcessor) => factory.Create(txProcessor))
                .AddScoped<IWithdrawalProcessor, AuraWithdrawalProcessor>()

                .AddSingleton<IBlockImprovementContextFactory, IBlockProducer, IMergeConfig>((blockProducer,
                    mergeConfig) => new BlockImprovementContextFactory(blockProducer, TimeSpan.FromSeconds(mergeConfig.SecondsPerSlot)))

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
        }

        protected override ChainSpec CreateChainSpec()
        {
            ChainSpec baseChainSpec = base.CreateChainSpec();
            baseChainSpec.EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new AuRaChainSpecEngineParameters
                {
                    WithdrawalContractAddress = new("0xbabe2bed00000000000000000000000000000003"),
                    StepDuration = { { 0, 3 } }
                });
            baseChainSpec.Parameters = new ChainParameters();
            return baseChainSpec;
        }

        protected override IBlockProcessor CreateBlockProcessor(IWorldState state)
        {
            _api = Container.Resolve<AuRaNethermindApi>();

            IBlockProcessor processor = _api.Context.Resolve<IAuRaBlockProcessorFactory>().Create(
                BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(TxProcessor), state),
                state,
                ReceiptStorage,
                new BeaconBlockRootHandler(TxProcessor, state),
                TxProcessor,
                MainExecutionRequestsProcessor,
                null,
                preWarmer: CreateBlockCachePreWarmer());

            return new TestBlockProcessorInterceptor(processor, _blockProcessingThrottle);
        }


        protected override IBlockProducer CreateTestBlockProducer()
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

            IBlockProducerEnv blockProducerEnv = BlockProducerEnvFactory.Create();
            PostMergeBlockProducer postMergeBlockProducer = blockProducerFactory.Create(blockProducerEnv);
            BlockProducer = postMergeBlockProducer;

            IAuRaStepCalculator auraStepCalculator = Substitute.For<IAuRaStepCalculator>();
            auraStepCalculator.TimeToNextStep.Returns(TimeSpan.FromMilliseconds(0));
            var env = BlockProducerEnvFactory.Create();
            FollowOtherMiners gasLimitCalculator = new(MainnetSpecProvider.Instance);
            AuRaBlockProducer preMergeBlockProducer = new(
                env.TxSource,
                env.ChainProcessor,
                env.ReadOnlyStateProvider,
                Sealer,
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
    }
}
