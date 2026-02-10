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
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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
    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null)
        => new MergeAuRaTestBlockchain(mergeConfig);

    protected override Hash256 ExpectedBlockHash => new("0x990d377b67dbffee4a60db6f189ae479ffb406e8abea16af55e0469b8524cf46");
    private const string _auraWithdrawalContractAddress = "0xbabe2bed00000000000000000000000000000003";

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

    [TestCase(
        "0x9bd79e1ac72667844a969b9584977d3f196082c31e5002eda0a7bd4da8d0e4ce",
        "0x176dff396d76839a9de72be37aa09076bff2b1c78908ae0f107036ea2de7c1c4",
        "0xdc17608835fdd8511c87c8fd36214735bb880a109124664756919e737674e070",
        "0x77a3fa6067dde61a",
        _auraWithdrawalContractAddress)]
    public override async Task Should_process_block_as_expected_V6(string latestValidHash, string blockHash, string stateRoot, string payloadId, string? auraWithdrawalContractAddress)
        => await base.Should_process_block_as_expected_V6(latestValidHash, blockHash, stateRoot, payloadId, auraWithdrawalContractAddress);

    [TestCase(
        "0xef18d85fde297e54998c706132658bdb8db2f43da55bc6cc42222b2758000ecc",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xdf1b2c48064fe2c6a431b32b76422d419fe4e3e744a2720d011bd134c5590e63")]
    public override Task NewPayloadV5_accepts_valid_BAL(string blockHash, string receiptsRoot, string stateRoot)
        => NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            auraWithdrawalContractAddress: _auraWithdrawalContractAddress);

    [TestCase(
        "0xc9e08f341474c4af262a47a18c37b95ab0d9cfd96d780ac6c2dd7d1362c43f04",
        "0xb6b4dddb39c5f23402fbc7e0e0ad387e24ea8b8d6e13b9e9f5f972ff064a82f6",
        "0x05a7a8b6afb54d9195d3c78c7b11febd7173ba93982a6d9cb981646fd4d723e0",
        "0xbc48a7a2b823d3a089a3d6fe46205e0ce1b642563fed1a8255005f64f4b5acac",
        _auraWithdrawalContractAddress)]
    public override Task NewPayloadV5_rejects_invalid_BAL_after_processing(string blockHash, string stateRoot, string invalidBalHash, string expectedBalHash, string? auraWithdrawalContractAddress)
        => base.NewPayloadV5_rejects_invalid_BAL_after_processing(blockHash, stateRoot, invalidBalHash, expectedBalHash, auraWithdrawalContractAddress);

    [TestCase(
        "0xc4ffe5a6af2fb1d97b9a58c4123040d44015fc9ca6e360baef75bc131cebfeb5",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public override Task NewPayloadV5_rejects_invalid_BAL_with_incorrect_changes_early(string blockHash, string receiptsRoot, string stateRoot)
        => NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            "InvalidBlockLevelAccessList: Suggested block-level access list contained incorrect changes for 0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8 at index 3.",
            withIncorrectChange: true,
            auraWithdrawalContractAddress: _auraWithdrawalContractAddress);

    [TestCase(
        "0x222582c7d7ef2f2e90ff4d689499847da742021839fd5aca1af5d4bc6f135ada",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public override Task NewPayloadV5_rejects_invalid_BAL_with_missing_changes_early(string blockHash, string receiptsRoot, string stateRoot)
        => NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            "InvalidBlockLevelAccessList: Suggested block-level access list missing account changes for 0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8 at index 2.",
            withMissingChange: true,
            auraWithdrawalContractAddress: _auraWithdrawalContractAddress);

    [TestCase(
        "0x78b7ff406febf51d4256d5e89ad00ee76903c768252eb2e1f5ef6c7f12da4fd8",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public override Task NewPayloadV5_rejects_invalid_BAL_with_surplus_changes_early(string blockHash, string receiptsRoot, string stateRoot)
        => NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            "InvalidBlockLevelAccessList: Suggested block-level access list contained surplus changes for 0x65942aaf2c32a1aca4f14e82e94fce91960893a2 at index 2.",
            withSurplusChange: true,
            auraWithdrawalContractAddress: _auraWithdrawalContractAddress);

    [TestCase(
        "0x65b0294a1727c92b65874c2505dbba3c290866d07f5a3ca4449cb283450ca692",
        "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569",
        "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b")]
    public override Task NewPayloadV5_rejects_invalid_BAL_with_surplus_reads_early(string blockHash, string receiptsRoot, string stateRoot)
        => NewPayloadV5(
            blockHash,
            receiptsRoot,
            stateRoot,
            "InvalidBlockLevelAccessList: Suggested block-level access list contained invalid storage reads.",
            withSurplusReads: true,
            auraWithdrawalContractAddress: _auraWithdrawalContractAddress);

    [Test]
    [TestCase(_auraWithdrawalContractAddress)]
    public override async Task GetPayloadV6_builds_block_with_BAL(string? auraWithdrawalContractAddress)
        => await base.GetPayloadV6_builds_block_with_BAL(auraWithdrawalContractAddress);

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
                    WithdrawalContractAddress = new(_auraWithdrawalContractAddress),
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
