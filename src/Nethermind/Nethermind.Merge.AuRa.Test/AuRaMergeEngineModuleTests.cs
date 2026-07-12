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
        "0xaa40d26a94de2ef75063d911a3f94eb655594e6219c670630762eb793683046c",
        "0x715332eb67484c964f3ab6e7c7b417cb35966dda287d9fb7ce2ef724d57e44b4",
        "0x3339423f06da988f138510ebd9dba1834d114181e1bfa459c8881dfe184a82b2",
        "0xebdb403215eb509f",
        _auraWithdrawalContractAddress)]
    public override async Task Should_process_block_as_expected_V6(string latestValidHash, string blockHash, string stateRoot, string payloadId, string? customWithdrawalContractAddress)
        => await base.Should_process_block_as_expected_V6(latestValidHash, blockHash, stateRoot, payloadId, customWithdrawalContractAddress);

    [TestCase("0x680ef2e7a94340086df97695296abbecd4ebbe531fc1b76befbc16beadf1a38e", "0x9a4312ed592f7dd89396b4a87f09cb501ccd451562c68979997ccc69d45bf9b3", "0x034ce8e962c2a85501f6ce88e7ab97884103b47d5c8afe1b33eb31d03fdbc403", false, false)]
    public override async Task NewPayloadV5_accepts_valid_BAL(string? blockHash, string? receiptsRoot, string? stateRoot, bool eip8037Enabled, bool useEnginePipeline)
        => await NewPayloadV5_via_manual_block(blockHash, receiptsRoot, stateRoot, customWithdrawalContractAddress: _auraWithdrawalContractAddress);

    [TestCase(
        "0x0b3f269183f353cca80bd3b31001a377e773782cc9577cb37e4581c98b76bddc",
        "0x914892da85e1a085a90e8a02f9a9cf0777d73c5798047c7324859b1c5ad9b67f",
        "0xae38a7762690e50ef3138c25e58b3f1cd6b41e7a6385c37fce6c8246d666dbf3",
        "0x423e51b670b1d00c6504c88a2b59158beeebcfffe088bc9d4dfb427f8f19d10a",
        _auraWithdrawalContractAddress,
        TestName = "NewPayloadV5_rejects_invalid_BAL_after_processing_AuRa_expanded")]
    [TestCase("0x0b3f269183f353cca80bd3b31001a377e773782cc9577cb37e4581c98b76bddc", "0x914892da85e1a085a90e8a02f9a9cf0777d73c5798047c7324859b1c5ad9b67f", "0xae38a7762690e50ef3138c25e58b3f1cd6b41e7a6385c37fce6c8246d666dbf3", "0x423e51b670b1d00c6504c88a2b59158beeebcfffe088bc9d4dfb427f8f19d10a", _auraWithdrawalContractAddress, TestName = "NewPayloadV5_rejects_invalid_BAL_after_processing_AuRa_inline")]
    public override Task NewPayloadV5_rejects_invalid_BAL_after_processing(string blockHash, string stateRoot, string invalidBalHash, string expectedBalHash, string? customWithdrawalContractAddress)
        => base.NewPayloadV5_rejects_invalid_BAL_after_processing(blockHash, stateRoot, invalidBalHash, expectedBalHash, customWithdrawalContractAddress);

    [TestCase("0xb29c7afdffb49efdfb356ca3e9408132b4af6a1f0e3e94f0792141772c9ba2b9", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false, BalErrorKind.IncorrectChange)]
    [TestCase("0xd5a6449ff8a7bf372a4b4e464a4b93c032ae285dc3e0fd780973a17b5ef5879d", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false, BalErrorKind.MissingChange)]
    [TestCase("0x090ba43f6056126e3d4ce35e14fd404f37175019dcb69dee5d2540d3d7a23efa", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false, BalErrorKind.SurplusChange)]
    [TestCase("0x538eede0c59801a98af49814bf11e5a16d69bffb881c958d1b23391b7c422eff", "0x3d4548dff4e45f6e7838b223bf9476cd5ba4fd05366e8cb4e6c9b65763209569", "0xd2e92dcdc98864f0cf2dbe7112ed1b0246c401eff3b863e196da0bfb0dec8e3b", false, false, BalErrorKind.SurplusReads)]
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
