// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
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
using Nethermind.AuRa.Test;
using NSubstitute;
using NUnit.Framework;
using Builders = Nethermind.Core.Test.Builders;

namespace Nethermind.Merge.AuRa.Test;

[TestFixture(true)]
[TestFixture(false)]
public class AuRaMergeEngineModuleTests(bool parallel) : EngineModuleTests(parallel)
{
    protected override MergeTestBlockchain CreateBaseBlockchain(IMergeConfig? mergeConfig = null)
    {
        MergeTestBlockchain bc = new MergeAuRaTestBlockchain(mergeConfig);
        bc.ParallelExecutionOverride = Parallel;
        return bc;
    }

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

    [TestCase("0xca2fbb93848df6500fcc33f9036f43f33db9844719f0a5fc69079d8d90dbb28f", "0x4b8e5a6567229461665f1475a39665a3df55b367ca5fd9cc861fe70d4d5836c3", "0xd4ab6af74f5566d54b164115a9b00726bd35e2170d206e466c4be30ebfe23894", "0x103ea062e6e09c06")]
    public override Task Should_process_block_as_expected_V2(string latestValidHash, string blockHash, string stateRoot, string payloadId)
        => base.Should_process_block_as_expected_V2(latestValidHash, blockHash, stateRoot, payloadId);

    [TestCase("0xe4333fcde906675e50500bf53a6c73bc51b2517509bc3cff2d24d0de9b8dd23e", "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926", "0xb22228e153345f9b")]
    public override Task processing_block_should_serialize_valid_responses(string blockHash, string latestValidHash, string payloadId)
        => base.processing_block_should_serialize_valid_responses(blockHash, latestValidHash, payloadId);

    [TestCase(
        "0x6a48eec80d3637d43c48957ef7248cd5d674fb21a71815dd929c95394b7d625a",
        "0x20b06a2d847a9aec7dd557e884e9944fec06783c45cf74ad1b77e9aca9817253",
        "0x9292b2b53d9dfc6f3b1f23d1ed2fdfef79b1be03a121d2b5191ea8bdcca71f5e",
        "0xa24fe41c710a4080",
        _auraWithdrawalContractAddress)]
    public override async Task Should_process_block_as_expected_V6(string latestValidHash, string blockHash, string stateRoot, string payloadId, string? customWithdrawalContractAddress)
        => await base.Should_process_block_as_expected_V6(latestValidHash, blockHash, stateRoot, payloadId, customWithdrawalContractAddress);

    [TestCase("0x5b65f4f07c872ab1fa6a49dee5860c6a256381a5f7f948c2cb165cf5c53a5f11", "0x9a4312ed592f7dd89396b4a87f09cb501ccd451562c68979997ccc69d45bf9b3", "0x30e58be6d5689f6aa9064c9ec4245d0505319fdaa4b0905bbb0bac789a8d232b", false, false)]
    public override async Task NewPayloadV5_accepts_valid_BAL(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline)
        => await NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, customWithdrawalContractAddress: _auraWithdrawalContractAddress);

    [TestCase(
        "0x863a34d4ab2b5cbd686c638e2c35f8357ecabbecc36d5f3ad094393ea7401611",
        "0x914892da85e1a085a90e8a02f9a9cf0777d73c5798047c7324859b1c5ad9b67f",
        "0xe64b6695a04ba31b0aa6e70c518b1e18661375ae642af9022ab0395fb774b62f",
        "0xa3b2b8d01a6afbde0a0030e74837333956f7d73ed060d4efa773cee308fd6078",
        _auraWithdrawalContractAddress,
        TestName = "NewPayloadV5_rejects_invalid_BAL_after_processing_AuRa_expanded")]
    [TestCase("0x863a34d4ab2b5cbd686c638e2c35f8357ecabbecc36d5f3ad094393ea7401611", "0x914892da85e1a085a90e8a02f9a9cf0777d73c5798047c7324859b1c5ad9b67f", "0xe64b6695a04ba31b0aa6e70c518b1e18661375ae642af9022ab0395fb774b62f", "0xa3b2b8d01a6afbde0a0030e74837333956f7d73ed060d4efa773cee308fd6078", _auraWithdrawalContractAddress, TestName = "NewPayloadV5_rejects_invalid_BAL_after_processing_AuRa_inline")]
    public override Task NewPayloadV5_rejects_invalid_BAL_after_processing(string blockHash, string stateRoot, string invalidBalHash, string expectedBalHash, string? customWithdrawalContractAddress)
        => base.NewPayloadV5_rejects_invalid_BAL_after_processing(blockHash, stateRoot, invalidBalHash, expectedBalHash, customWithdrawalContractAddress);

    [TestCase("0xf5a73d9eba949d1b4a95b58a0004c97c1b0c67928446f70346390f5e5dc76b56", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false, BalErrorKind.IncorrectChange)]
    [TestCase("0xef2f3ffe8ae0a8f60c244a198af6bd5be3178e3d3b431be62b800e2b9bebcebf", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false, BalErrorKind.MissingChange)]
    [TestCase("0xcb9cfe11e8e07b1f64560f6a3c97b3aca6e62ca35f9a39bea499f4f18423257b", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false, BalErrorKind.SurplusChange)]
    [TestCase("0x782be526a797b52c6013c261503e4d800b14d9df9cefb1ad33965c7d6842ac2b", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false, BalErrorKind.SurplusReads)]
    public override Task NewPayloadV5_rejects_invalid_BAL_early(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline, BalErrorKind errorKind) =>
        NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, GetExpectedBalError(errorKind), errorKind, customWithdrawalContractAddress: _auraWithdrawalContractAddress);

    [Test]
    [TestCase(_auraWithdrawalContractAddress)]
    public override async Task GetPayloadV6_builds_block_with_BAL(string? customWithdrawalContractAddress) =>
        await base.GetPayloadV6_builds_block_with_BAL(customWithdrawalContractAddress);

    [Test]
    public override async Task GetPayloadBodiesHashV2_returns_correctly()
        => await base.GetPayloadBodiesHashV2_returns_correctly();

    [Test]
    public override async Task GetPayloadBodiesByRangeV2_returns_correctly()
        => await base.GetPayloadBodiesByRangeV2_returns_correctly();

    [Test]
    public override async Task Can_build_and_process_multiple_blocks_V6()
        => await base.Can_build_and_process_multiple_blocks_V6();

    [TestCase("0xa66ec67b117f57388da53271f00c22a68e6c297b564f67c5904e6f2662881875", "0xe168b70ac8a6f7d90734010030801fbb2dcce03a657155c4024b36ba8d1e3926")]
    [Parallelizable(ParallelScope.None)]
    [Obsolete]
    public override Task forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http(string blockHash, string parentHash)
        => base.forkchoiceUpdatedV1_should_communicate_with_boost_relay_through_http(blockHash, parentHash);

    [Ignore("Withdrawals are not withdrawn due to lack of Aura contract in tests")]
    public override Task Can_apply_withdrawals_correctly((Withdrawal[][] Withdrawals, (Address Account, UInt256 BalanceIncrease)[] ExpectedAccountIncrease) input) =>
        base.Can_apply_withdrawals_correctly(input);

    [Test]
    [Category("Flaky"), Retry(3)]
    [NonParallelizable]
    [Platform(Exclude = "MacOsX", Reason = "Timing-sensitive 10ms delays too tight on macOS ARM runners")]
    public Task AuRa_getPayloadV1_does_not_wait_for_improvement_when_block_is_not_empty()
        => base.getPayloadV1_does_not_wait_for_improvement_when_block_is_not_empty();

    protected override BlockBuilder BuildNewBlock(Block head)
        => base.BuildNewBlock(head).WithAura(0, []);

    protected override BlockBuilder BuildOneMoreTerminalBlock(Block head, bool correctStateRoot = true)
        => base.BuildOneMoreTerminalBlock(head, correctStateRoot).WithAura(0, []);

    public class MergeAuRaTestBlockchain : MergeTestBlockchain
    {
        public MergeAuRaTestBlockchain(IMergeConfig? mergeConfig = null)
            : base(mergeConfig) =>
            SealEngineType = Core.SealEngineType.AuRa;

        // Install AuRaMergeModule below (after AuRaModule, so its last-wins registrations take effect)
        // rather than via TestMergeModule, so BaseMergePluginModule loads exactly once (as in production).
        protected override IModule? MergeModule => null;

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddDecorator<ISpecProvider>((_, specProvider) =>
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
                // as normally, test blockchain don't use INethermindApi at all.
                .AddModule(new AuRaModule(ChainSpec))

                .AddSingleton<ISigner>(NullSigner.Instance)
                .AddModule(new AuRaMergeModule())
                .AddSingleton<NethermindApi.Dependencies>()
                .AddSingleton<IReportingValidator>(NullReportingValidator.Instance)
                .AddSingleton<ISealer>(NullSealEngine.Instance) // Test not originally made with aura sealer

                .AddScoped<WithdrawalContractFactory>()
                .AddScoped<IWithdrawalContract, WithdrawalContractFactory, ITransactionProcessor>((factory, txProcessor) => factory.Create(txProcessor))
                .AddScoped<IWithdrawalProcessor, AuraWithdrawalProcessor>()
                .AddScoped<IWithdrawalProcessorFactory, AuraWithdrawalProcessorFactory>()

                .AddSingleton<IBlockImprovementContextFactory, IBlockProducer, IMergeConfig>((blockProducer,
                    mergeConfig) => new BlockImprovementContextFactory(blockProducer, TimeSpan.FromSeconds(mergeConfig.SecondsPerSlot)))

                .AddSingleton<IAuRaBlockFinalizationManager>(Substitute.For<IAuRaBlockFinalizationManager>())

                .AddDecorator<AuRaNethermindApi>((_, api) =>
                {
                    // Yes getting from `TestBlockchain` itself, since steps are not run
                    // and some of these are not from DI. you know... chicken and egg, but don't forget about the rooster.
                    api.TxPool = TxPool;
                    api.TransactionComparerProvider = TransactionComparerProvider;
                    return api;
                });

        protected override ChainSpec CreateChainSpec()
        {
            ChainSpec baseChainSpec = base.CreateChainSpec();
            baseChainSpec.Genesis = Builders.Build.A.Block
                .WithDifficulty(0)
                .WithAura(0, new byte[65])
                .TestObject;
            AuRaChainSpecEngineParameters.AuRaValidatorJson validatorsJson = new()
            {
                List = [Address.Zero]
            };
            baseChainSpec.EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new AuRaChainSpecEngineParameters
                {
                    WithdrawalContractAddress = new(_auraWithdrawalContractAddress),
                    StepDuration = { { 0, 3 } },
                    ValidatorsJson = validatorsJson
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

            IBlockProducerEnv blockProducerEnv = BlockProducerEnvFactory.CreatePersistent();
            PostMergeBlockProducer postMergeBlockProducer = blockProducerFactory.Create(blockProducerEnv);
            BlockProducer = postMergeBlockProducer;

            IAuRaStepCalculator auraStepCalculator = Substitute.For<IAuRaStepCalculator>();
            auraStepCalculator.TimeToNextStep.Returns(TimeSpan.FromMilliseconds(0));
            IBlockProducerEnv env = BlockProducerEnvFactory.CreatePersistent();
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
