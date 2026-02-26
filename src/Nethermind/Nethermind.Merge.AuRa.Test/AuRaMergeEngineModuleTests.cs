// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm.TransactionProcessing;
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
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeEngineModuleTests : EngineModuleTests
{
    private static readonly Address AuraWithdrawalContractAddress = new("0xbabe2bed00000000000000000000000000000003");
    private static readonly Address AuraSystemAddress = new("0xfffffffffffffffffffffffffffffffffffffffe");

    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null)
        => new MergeAuRaTestBlockchain(mergeConfig);

    protected override Hash256 ExpectedBlockHash => new("0x990d377b67dbffee4a60db6f189ae479ffb406e8abea16af55e0469b8524cf46");
    protected override int ExpectedPayloadBodiesV2BlockAccessListByteLength => 364;

    protected override AccountChanges[] GetAdditionalPrecompileAccountChanges() =>
    [
        Build.An.AccountChanges.WithAddress(AuraWithdrawalContractAddress).TestObject,
        Build.An.AccountChanges.WithAddress(AuraSystemAddress).TestObject
    ];

    [TestCaseSource(nameof(GetWithdrawalValidationValues))]
    public override Task forkchoiceUpdatedV2_should_validate_withdrawals((IReleaseSpec Spec,
        string ErrorMessage,
        Withdrawal[]? Withdrawals,
        string BlockHash,
        int ErrorCode
        ) input)
        => base.forkchoiceUpdatedV2_should_validate_withdrawals(input);

    [TestCase(
        "0x76d560a6eb4ea2dd6232cc7feb6f61393d9e955baffaf3a84b1c53d3dd0746b8",
        "0x2277308bcf798426afd925e4f67303af26c83f5c1832644c4de93893d41bdbf5",
        "0xae4bd586033cba409a0c6e58c4a0808ef798747cd55a781b972e6e00b9427af8",
        "0xf0954948630d827c")]
    public override Task Should_process_block_as_expected_V4(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V4(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0xec6f5611ce3652fefd669e8d7e6d63bd8cdefdcdfe9a0a44eb61355084831da4",
        "0xf382f220de54b57ac9355d4eeb114f9e6bc4d25e307cdac0347b43d5534ac68e",
        "0xb8a1a0780980ab4e20a46237a3c533af8cd0386cf4c74d05c8ec5e9bf5cbc482",
        "0x2802e8a8c34cd1ea")]
    public override Task Should_process_block_as_expected_V6(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V6(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase(
        "0x8d8fa83b6c3adb40b8f2e1f4798e9d900564b8548e209a65c8cd29fde02e7a32",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0x3e98244425fbc5413150a01fd823bece9ae66ef182f11597f0abdfd251d9aa16")]
    public override Task NewPayloadV5_accepts_valid_BAL(string blockHash, string receiptsRoot, string stateRoot)
        => base.NewPayloadV5_accepts_valid_BAL(blockHash, receiptsRoot, stateRoot);

    [TestCase(
        "0xcb284697c29b108d9820ab65ca8b44c6abc50f2598f9412dbfc88d84db5e6fc5",
        "0x914892da85e1a085a90e8a02f9a9cf0777d73c5798047c7324859b1c5ad9b67f",
        "0xf33cd1904c18109e882bfa965997ba802d408bd834a61920aba651fbaeb78dd3")]
    public override Task NewPayloadV5_rejects_invalid_BAL(string blockHash, string stateRoot, string balHash)
        => base.NewPayloadV5_rejects_invalid_BAL(blockHash, stateRoot, balHash);

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

    [Ignore("Withdrawals are not withdrawn due to lack of Aura contract in tests")]
    public override Task Can_apply_withdrawals_correctly((Withdrawal[][] Withdrawals, (Address Account, UInt256 BalanceIncrease)[] ExpectedAccountIncrease) input)
    {
        return base.Can_apply_withdrawals_correctly(input);
    }

    public class MergeAuRaTestBlockchain : MergeTestBlockchain
    {
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
                .WithGenesisPostProcessor((block, state) =>
                {
                    block.Header.AuRaStep = 0;
                    block.Header.AuRaSignature = new byte[65];
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

                // AuRa was never configured correctly in test.
                .AddScoped<IBlockProcessor, BlockProcessor>()

                .AddDecorator<AuRaNethermindApi>((ctx, api) =>
                {
                    // Yes getting from `TestBlockchain` itself, since steps are not run
                    // and some of these are not from DI. you know... chicken and egg, but don't forget about the rooster.
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
                    WithdrawalContractAddress = AuraWithdrawalContractAddress,
                    StepDuration = { { 0, 3 } }
                });
            baseChainSpec.Parameters = new ChainParameters();
            return baseChainSpec;
        }

        protected override IBlockProducer CreateTestBlockProducer()
        {
            BlocksConfig blocksConfig = new() { MinGasPrice = 0 };
            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(SpecProvider, blocksConfig);
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
