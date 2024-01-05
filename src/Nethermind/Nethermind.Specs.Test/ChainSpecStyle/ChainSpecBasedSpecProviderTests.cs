// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class ChainSpecBasedSpecProviderTests
{
    private const double GnosisBlockTime = 5;

    [TestCase(0, null, false)]
    [TestCase(0, 0ul, false)]
    [TestCase(0, 4660ul, false)]
    [TestCase(1, 4660ul, false)]
    [TestCase(1, 4661ul, false)]
    [TestCase(4, 4672ul, true)]
    [TestCase(4, 4673ul, true)]
    [TestCase(5, 4680ul, true)]
    [NonParallelizable]
    public void Timstamp_activation_equal_to_genesis_timestamp_loads_correctly(long blockNumber, ulong? timestamp, bool isEip3855Enabled)
    {
        ChainSpecLoader loader = new(new EthereumJsonSerializer());
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"../../../../{Assembly.GetExecutingAssembly().GetName().Name}/Specs/Timstamp_activation_equal_to_genesis_timestamp_test.json");
        ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
        chainSpec.Parameters.Eip2537Transition.Should().BeNull();
        var logger = Substitute.ForPartsOf<LimboTraceLogger>();
        var logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<ChainSpecBasedSpecProvider>().Returns(logger);
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        ReleaseSpec expectedSpec = ((ReleaseSpec)MainnetSpecProvider
            .Instance.GetSpec((MainnetSpecProvider.GrayGlacierBlockNumber, null))).Clone();
        expectedSpec.Name = "Genesis_with_non_zero_timestamp";
        expectedSpec.IsEip3651Enabled = true;
        expectedSpec.IsEip3198Enabled = false;
        expectedSpec.Eip1559TransitionBlock = 0;
        expectedSpec.DifficultyBombDelay = 0;
        expectedSpec.IsEip3855Enabled = isEip3855Enabled;
        TestSpecProvider testProvider = TestSpecProvider.Instance;
        testProvider.SpecToReturn = expectedSpec;
        testProvider.TerminalTotalDifficulty = 0;
        testProvider.GenesisSpec = expectedSpec;

        CompareSpecs(testProvider, provider, (blockNumber, timestamp));
        Assert.That(provider.GenesisSpec.Eip1559TransitionBlock, Is.EqualTo(testProvider.GenesisSpec.Eip1559TransitionBlock));
        Assert.That(provider.GenesisSpec.DifficultyBombDelay, Is.EqualTo(testProvider.GenesisSpec.DifficultyBombDelay));
    }


    [TestCase(0, null, false, false, false)]
    [TestCase(0, 0ul, false, false, false)]
    [TestCase(0, 4660ul, false, false, false)]
    [TestCase(1, 4660ul, false, false, false)]
    [TestCase(1, 4661ul, false, false, false)]
    [TestCase(1, 4672ul, false, false, false)]
    [TestCase(2, 4673ul, false, false, true)]
    [TestCase(3, 4680ul, false, false, true)]
    [TestCase(4, 4672ul, false, true, false)]
    [TestCase(5, 4672ul, true, true, false)]
    [TestCase(5, 4673ul, true, true, false)]
    [TestCase(6, 4680ul, true, true, false)]
    [NonParallelizable]
    public void Logs_warning_when_timestampActivation_happens_before_blockActivation(long blockNumber, ulong? timestamp, bool isEip3855Enabled, bool isEip3198Enabled, bool receivesWarning)
    {
        ChainSpecLoader loader = new(new EthereumJsonSerializer());
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"../../../../{Assembly.GetExecutingAssembly().GetName().Name}/Specs/Logs_warning_when_timestampActivation_happens_before_blockActivation_test.json");
        ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
        chainSpec.Parameters.Eip2537Transition.Should().BeNull();
        var logger = Substitute.For<ILogger>();
        logger.IsWarn.Returns(true);
        var logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<ChainSpecBasedSpecProvider>().Returns(logger);
        ChainSpecBasedSpecProvider provider = new(chainSpec, logManager);
        ReleaseSpec expectedSpec = ((ReleaseSpec)MainnetSpecProvider
            .Instance.GetSpec((MainnetSpecProvider.GrayGlacierBlockNumber, null))).Clone();
        expectedSpec.Name = "Genesis_with_non_zero_timestamp";
        expectedSpec.IsEip3651Enabled = true;
        expectedSpec.IsEip3198Enabled = isEip3198Enabled;
        expectedSpec.IsEip3855Enabled = isEip3855Enabled;
        expectedSpec.Eip1559TransitionBlock = 0;
        expectedSpec.DifficultyBombDelay = 0;
        List<ForkActivation> forkActivationsToTest = new()
        {
            (blockNumber, timestamp),
        };

        foreach (ForkActivation activation in forkActivationsToTest)
        {
            provider.GetSpec(activation);
        }

        if (receivesWarning)
        {
            logger.Received(1).Warn(Arg.Is("Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition."));
        }
        else
        {
            logger.DidNotReceive().Warn(Arg.Is("Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition."));
        }
    }

    public static IEnumerable<TestCaseData> SepoliaActivations
    {
        get
        {
            yield return new TestCaseData((ForkActivation)(2, 0)) { TestName = "First" };
            yield return new TestCaseData((ForkActivation)(120_000_000, 0)) { TestName = "Block number" };
            yield return new TestCaseData((ForkActivation)(1735372, 3)) { TestName = "Low timestamp" };
            yield return new TestCaseData((ForkActivation)(1735372, 1677557088)) { TestName = "1677557088" };
            yield return new TestCaseData((ForkActivation)(1735372, 1677557087)) { TestName = "1677557087" };
        }
    }

    [TestCaseSource(nameof(SepoliaActivations))]
    public void Sepolia_loads_properly(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("sepolia");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        SepoliaSpecProvider sepolia = SepoliaSpecProvider.Instance;

        CompareSpecs(sepolia, provider, forkActivation);
        Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(SepoliaSpecProvider.Instance.TerminalTotalDifficulty));
        Assert.That(provider.GenesisSpec.Eip1559TransitionBlock, Is.EqualTo(0));
        Assert.That(provider.GenesisSpec.DifficultyBombDelay, Is.EqualTo(long.MaxValue));
        Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Sepolia));
        Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Sepolia));

        GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
            t => ValidateSlotByTimestamp(t, SepoliaSpecProvider.BeaconChainGenesisTimestamp).Should().BeTrue());
    }

    public static IEnumerable<TestCaseData> HoleskyActivations
    {
        get
        {
            yield return new TestCaseData(new ForkActivation(0, HoleskySpecProvider.GenesisTimestamp)) { TestName = "Genesis" };
            yield return new TestCaseData(new ForkActivation(1, HoleskySpecProvider.ShanghaiTimestamp)) { TestName = "Shanghai" };
            yield return new TestCaseData(new ForkActivation(3, HoleskySpecProvider.ShanghaiTimestamp + 24)) { TestName = "Post Shanghai" };
            //new ForkActivation(4, HoleskySpecProvider.CancunTimestamp),
            //new ForkActivation(5, HoleskySpecProvider.CancunTimestamp + 12),
        }
    }

    [TestCaseSource(nameof(HoleskyActivations))]
    public void Holesky_loads_properly(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("holesky");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        ISpecProvider hardCodedSpec = HoleskySpecProvider.Instance;

        CompareSpecs(hardCodedSpec, provider, forkActivation);
        Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(hardCodedSpec.TerminalTotalDifficulty));
        Assert.That(provider.GenesisSpec.Eip1559TransitionBlock, Is.EqualTo(0));
        Assert.That(provider.GenesisSpec.DifficultyBombDelay, Is.EqualTo(0));
        Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Holesky));
        Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Holesky));

        // because genesis time for holesky is set 5 minutes before the launch of the chain. this test fails.
        //GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
        //    t => ValidateSlotByTimestamp(t, HoleskySpecProvider.GenesisTimestamp).Should().BeTrue());
    }

    public static IEnumerable<TestCaseData> GoerliActivations
    {
        get
        {
            yield return new TestCaseData((ForkActivation)0) { TestName = "Genesis" };
            yield return new TestCaseData((ForkActivation)1) { TestName = "1" };
            yield return new TestCaseData((ForkActivation)(GoerliSpecProvider.IstanbulBlockNumber - 1)) { TestName = "Before Istanbul" };
            yield return new TestCaseData((ForkActivation)GoerliSpecProvider.IstanbulBlockNumber) { TestName = "Istanbul" };
            yield return new TestCaseData((ForkActivation)(GoerliSpecProvider.BerlinBlockNumber - 1)) { TestName = "Before Berlin" };
            yield return new TestCaseData((ForkActivation)GoerliSpecProvider.BerlinBlockNumber) { TestName = "Berlin" };
            yield return new TestCaseData((ForkActivation)(GoerliSpecProvider.LondonBlockNumber - 1)) { TestName = "Before London" };
            yield return new TestCaseData((ForkActivation)GoerliSpecProvider.LondonBlockNumber) { TestName = "London" };
            yield return new TestCaseData(new ForkActivation(GoerliSpecProvider.LondonBlockNumber + 1, GoerliSpecProvider.ShanghaiTimestamp - 1)) { TestName = "Before Shanghai" };
            yield return new TestCaseData(new ForkActivation(GoerliSpecProvider.LondonBlockNumber + 1, GoerliSpecProvider.ShanghaiTimestamp)) { TestName = "Shanghai" };
            yield return new TestCaseData(new ForkActivation(GoerliSpecProvider.LondonBlockNumber + 2, GoerliSpecProvider.CancunTimestamp - 1)) { TestName = "Before Cancun" };
            yield return new TestCaseData(new ForkActivation(GoerliSpecProvider.LondonBlockNumber + 2, GoerliSpecProvider.CancunTimestamp)) { TestName = "Cancun" };
            yield return new TestCaseData(new ForkActivation(GoerliSpecProvider.LondonBlockNumber + 2, GoerliSpecProvider.CancunTimestamp + 100000000)) { TestName = "Future" };
        }
    }

    [TestCaseSource(nameof(GoerliActivations))]
    public void Goerli_loads_properly(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("goerli");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        GoerliSpecProvider goerli = GoerliSpecProvider.Instance;

        CompareSpecs(goerli, provider, forkActivation);
        Assert.That(provider.GenesisSpec.Eip1559TransitionBlock, Is.EqualTo(GoerliSpecProvider.LondonBlockNumber));
        Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(GoerliSpecProvider.Instance.TerminalTotalDifficulty));
        Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Goerli));
        Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Goerli));

        GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
            t => ValidateSlotByTimestamp(t, GoerliSpecProvider.BeaconChainGenesisTimestamp).Should().BeTrue());
    }

    public static IEnumerable<TestCaseData> ChiadoActivations
    {
        get
        {
            yield return new TestCaseData((ForkActivation)0) { TestName = "Genesis" };
            yield return new TestCaseData((ForkActivation)(1, 20)) { TestName = "(1, 20)" };
            yield return new TestCaseData((ForkActivation)(1, ChiadoSpecProvider.ShanghaiTimestamp - 1)) { TestName = "Before Shanghai" };
            yield return new TestCaseData((ForkActivation)(1, ChiadoSpecProvider.ShanghaiTimestamp)) { TestName = "Shanghai" };
            yield return new TestCaseData((ForkActivation)(1, ChiadoSpecProvider.CancunTimestamp - 1)) { TestName = "Before Cancun" };
            yield return new TestCaseData((ForkActivation)(1, ChiadoSpecProvider.CancunTimestamp)) { TestName = "Cancun" };
            yield return new TestCaseData((ForkActivation)(1, ChiadoSpecProvider.CancunTimestamp + 100000000)) { TestName = "Future" };
        }
    }

    [TestCaseSource(nameof(ChiadoActivations))]
    public void Chiado_loads_properly(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("chiado");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        ChiadoSpecProvider chiado = ChiadoSpecProvider.Instance;

        CompareSpecs(chiado, provider, forkActivation, CompareSpecsOptions.IsGnosis);
        Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(ChiadoSpecProvider.Instance.TerminalTotalDifficulty));
        Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Chiado));
        Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Chiado));

        IReleaseSpec? preShanghaiSpec = provider.GetSpec((1, ChiadoSpecProvider.ShanghaiTimestamp - 1));
        IReleaseSpec? postShanghaiSpec = provider.GetSpec((1, ChiadoSpecProvider.ShanghaiTimestamp));

        VerifyGnosisShanghaiSpecifics(preShanghaiSpec, postShanghaiSpec);
        VerifyGnosisCancunSpecifics();
        GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
            t => ValidateSlotByTimestamp(t, ChiadoSpecProvider.BeaconChainGenesisTimestamp, GnosisBlockTime).Should().BeTrue());
    }

    public static IEnumerable<TestCaseData> GnosisActivations
    {
        get
        {
            yield return new TestCaseData((ForkActivation)0) { TestName = "Genesis" };
            yield return new TestCaseData((ForkActivation)1) { TestName = "Genesis + 1" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.ConstantinopoleBlockNumber - 1)) { TestName = "Before Constantinopole" };
            yield return new TestCaseData((ForkActivation)GnosisSpecProvider.ConstantinopoleBlockNumber) { TestName = "Constantinopole" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.ConstantinopoleFixBlockNumber - 1)) { TestName = "Before ConstantinopoleFix" };
            yield return new TestCaseData((ForkActivation)GnosisSpecProvider.ConstantinopoleFixBlockNumber) { TestName = "ConstantinopoleFix" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.IstanbulBlockNumber - 1)) { TestName = "Before Istanbul" };
            yield return new TestCaseData((ForkActivation)GnosisSpecProvider.IstanbulBlockNumber) { TestName = "Istanbul" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.BerlinBlockNumber - 1)) { TestName = "Before Berlin" };
            yield return new TestCaseData((ForkActivation)GnosisSpecProvider.BerlinBlockNumber) { TestName = "Berlin" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber - 1)) { TestName = "Before London" };
            yield return new TestCaseData((ForkActivation)GnosisSpecProvider.LondonBlockNumber) { TestName = "London" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber, GnosisSpecProvider.ShanghaiTimestamp - 1)) { TestName = "Before Shanghai" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber, GnosisSpecProvider.ShanghaiTimestamp)) { TestName = "Shanghai" };
            yield return new TestCaseData((ForkActivation)(999_999_999, 999_999_999)) { TestName = "Future" };
        }
    }

    [TestCaseSource(nameof(GnosisActivations))]
    public void Gnosis_loads_properly(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("gnosis");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        GnosisSpecProvider gnosisSpecProvider = GnosisSpecProvider.Instance;

        CompareSpecs(gnosisSpecProvider, provider, forkActivation, CompareSpecsOptions.IsGnosis);
        Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(GnosisSpecProvider.Instance.TerminalTotalDifficulty));
        Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Gnosis));
        Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Gnosis));

        VerifyGnosisPreShanghaiSpecifics(provider);

        IReleaseSpec? preShanghaiSpec = provider.GetSpec((GnosisSpecProvider.LondonBlockNumber + 1,
            GnosisSpecProvider.ShanghaiTimestamp - 1));
        IReleaseSpec? postShanghaiSpec = provider.GetSpec((GnosisSpecProvider.LondonBlockNumber + 1,
            GnosisSpecProvider.ShanghaiTimestamp));

        VerifyGnosisShanghaiSpecifics(preShanghaiSpec, postShanghaiSpec);
        VerifyGnosisCancunSpecifics();
        GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
            t => ValidateSlotByTimestamp(t, GnosisSpecProvider.BeaconChainGenesisTimestamp, GnosisBlockTime).Should().BeTrue());
    }

    private static void VerifyGnosisCancunSpecifics()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Eip4844Constants.BlobGasPriceUpdateFraction, Is.EqualTo((UInt256)1112826));
            Assert.That(Eip4844Constants.MaxBlobGasPerBlock, Is.EqualTo(262144));
            Assert.That(Eip4844Constants.MinBlobGasPrice, Is.EqualTo(1.GWei()));
            Assert.That(Eip4844Constants.TargetBlobGasPerBlock, Is.EqualTo(131072));
        });
    }

    private static void VerifyGnosisShanghaiSpecifics(IReleaseSpec preShanghaiSpec, IReleaseSpec postShanghaiSpec)
    {
        preShanghaiSpec.MaxCodeSize.Should().Be(long.MaxValue);
        postShanghaiSpec.MaxCodeSize.Should().Be(24576L);

        preShanghaiSpec.MaxInitCodeSize.Should().Be(-2L); // doesn't have meaningful value before EIP3860
        postShanghaiSpec.MaxInitCodeSize.Should().Be(2 * 24576L);

        preShanghaiSpec.LimitCodeSize.Should().Be(false);
        postShanghaiSpec.LimitCodeSize.Should().Be(true);

        preShanghaiSpec.IsEip170Enabled.Should().Be(false);
        postShanghaiSpec.IsEip170Enabled.Should().Be(true);
    }

    private static void VerifyGnosisPreShanghaiSpecifics(ISpecProvider specProvider)
    {
        specProvider.GenesisSpec.MaximumUncleCount.Should().Be(0);
        specProvider.GetSpec((ForkActivation)(GnosisSpecProvider.ConstantinopoleBlockNumber - 1)).IsEip1283Enabled.Should()
            .BeFalse();
        specProvider.GetSpec((ForkActivation)GnosisSpecProvider.ConstantinopoleBlockNumber).IsEip1283Enabled.Should()
            .BeTrue();
        specProvider.GetSpec((ForkActivation)(GnosisSpecProvider.ConstantinopoleBlockNumber - 1)).UseConstantinopleNetGasMetering.Should()
            .BeFalse();
        specProvider.GetSpec((ForkActivation)GnosisSpecProvider.ConstantinopoleBlockNumber).UseConstantinopleNetGasMetering.Should()
            .BeTrue();
    }

    public static IEnumerable<TestCaseData> MainnetActivations
    {
        get
        {
            yield return new TestCaseData((ForkActivation)0) { TestName = "Genesis" };
            yield return new TestCaseData((ForkActivation)(0, null)) { TestName = "Genesis null" };
            yield return new TestCaseData((ForkActivation)(0, 0)) { TestName = "Genesis timestamp" };
            yield return new TestCaseData((ForkActivation)1) { TestName = "Genesis + 1" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.HomesteadBlockNumber - 1)) { TestName = "Before Homestead" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.HomesteadBlockNumber) { TestName = "Homestead" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.TangerineWhistleBlockNumber - 1)) { TestName = "Before TangerineWhistle" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.TangerineWhistleBlockNumber) { TestName = "TangerineWhistle" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.SpuriousDragonBlockNumber - 1)) { TestName = "Before SpuriousDragon" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.SpuriousDragonBlockNumber) { TestName = "SpuriousDragon" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.ByzantiumBlockNumber - 1)) { TestName = "Before Byzantium" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.ByzantiumBlockNumber) { TestName = "Byzantium" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.ConstantinopleFixBlockNumber - 1)) { TestName = "Before Constantinople" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber) { TestName = "Constantinople" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.IstanbulBlockNumber - 1)) { TestName = "Before Istanbul" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber) { TestName = "Istanbul" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.MuirGlacierBlockNumber - 1)) { TestName = "Before MuirGlacier" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.MuirGlacierBlockNumber) { TestName = "MuirGlacier" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.BerlinBlockNumber - 1)) { TestName = "Before Berlin" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.BerlinBlockNumber) { TestName = "Berlin" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.LondonBlockNumber - 1)) { TestName = "Before London" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.LondonBlockNumber) { TestName = "London" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.ArrowGlacierBlockNumber - 1)) { TestName = "Before ArrowGlacier" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.ArrowGlacierBlockNumber) { TestName = "ArrowGlacier" };
            yield return new TestCaseData((ForkActivation)(MainnetSpecProvider.ArrowGlacierBlockNumber - 1)) { TestName = "Before GrayGlacier" };
            yield return new TestCaseData((ForkActivation)MainnetSpecProvider.ArrowGlacierBlockNumber) { TestName = "GrayGlacier" };
            yield return new TestCaseData(MainnetSpecProvider.ShanghaiActivation) { TestName = "Shanghai" };
            yield return new TestCaseData(new ForkActivation(99_000_000, 99_681_338_455)) { TestName = "Future" };
        }
    }

    [TestCaseSource(nameof(MainnetActivations))]
    public void Mainnet_loads_properly(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("foundation");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        MainnetSpecProvider mainnet = MainnetSpecProvider.Instance;

        CompareSpecs(mainnet, provider, forkActivation, CompareSpecsOptions.CheckDifficultyBomb);
        provider.GetSpec((MainnetSpecProvider.SpuriousDragonBlockNumber, null)).MaxCodeSize.Should().Be(24576L);
        provider.GetSpec((MainnetSpecProvider.SpuriousDragonBlockNumber, null)).MaxInitCodeSize.Should().Be(2 * 24576L);

        provider.GetSpec((ForkActivation)(long.MaxValue - 1)).IsEip2537Enabled.Should().BeFalse();
        Assert.That(provider.GenesisSpec.Eip1559TransitionBlock, Is.EqualTo(MainnetSpecProvider.LondonBlockNumber));
        Assert.That(provider.GetSpec((ForkActivation)4_369_999).DifficultyBombDelay, Is.EqualTo(0_000_000));
        Assert.That(provider.GetSpec((ForkActivation)4_370_000).DifficultyBombDelay, Is.EqualTo(3_000_000));
        Assert.That(provider.GetSpec((ForkActivation)7_279_999).DifficultyBombDelay, Is.EqualTo(3_000_000));
        Assert.That(provider.GetSpec((ForkActivation)7_279_999).DifficultyBombDelay, Is.EqualTo(3_000_000));
        Assert.That(provider.GetSpec((ForkActivation)7_280_000).DifficultyBombDelay, Is.EqualTo(5_000_000));
        Assert.That(provider.GetSpec((ForkActivation)9_199_999).DifficultyBombDelay, Is.EqualTo(5_000_000));
        Assert.That(provider.GetSpec((ForkActivation)9_200_000).DifficultyBombDelay, Is.EqualTo(9_000_000));
        Assert.That(provider.GetSpec((ForkActivation)12_000_000).DifficultyBombDelay, Is.EqualTo(9_000_000));
        Assert.That(provider.GetSpec((ForkActivation)12_964_999).DifficultyBombDelay, Is.EqualTo(9_000_000));
        Assert.That(provider.GetSpec((ForkActivation)12_965_000).DifficultyBombDelay, Is.EqualTo(9_700_000));
        Assert.That(provider.GetSpec((ForkActivation)13_772_999).DifficultyBombDelay, Is.EqualTo(9_700_000));
        Assert.That(provider.GetSpec((ForkActivation)13_773_000).DifficultyBombDelay, Is.EqualTo(10_700_000));
        Assert.That(provider.GetSpec((ForkActivation)15_049_999).DifficultyBombDelay, Is.EqualTo(10_700_000));
        Assert.That(provider.GetSpec((ForkActivation)15_050_000).DifficultyBombDelay, Is.EqualTo(11_400_000));
        Assert.That(provider.GetSpec((ForkActivation)99_414_000).DifficultyBombDelay, Is.EqualTo(11_400_000));
        Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(MainnetSpecProvider.Instance.TerminalTotalDifficulty));
        Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Mainnet));
        Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Mainnet));

        GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
            t => ValidateSlotByTimestamp(t, MainnetSpecProvider.BeaconChainGenesisTimestamp).Should().BeTrue());
    }

    [Flags]
    enum CompareSpecsOptions
    {
        None = 0,
        IsMainnet = 1,
        CheckDifficultyBomb = 2,
        IsGnosis = 4 // for Gnosis and Chiado testnets
    }

    private static void CompareSpecs(
        ISpecProvider oldSpecProvider,
        ISpecProvider newSpecProvider,
        ForkActivation activation,
        CompareSpecsOptions compareSpecsOptions = CompareSpecsOptions.None)
    {
        IReleaseSpec oldSpec = oldSpecProvider.GetSpec(activation);
        IReleaseSpec newSpec = newSpecProvider.GetSpec(activation);
        long? daoBlockNumber = newSpecProvider.DaoBlockNumber;

        bool isMainnet = daoBlockNumber is not null;
        if (isMainnet)
            compareSpecsOptions |= CompareSpecsOptions.IsMainnet;

        CompareSpecs(oldSpec, newSpec, activation, compareSpecsOptions);
    }

    private static void CompareSpecs(IReleaseSpec expectedSpec, IReleaseSpec actualSpec, ForkActivation activation, CompareSpecsOptions compareSpecsOptions)
    {
        bool isMainnet = (compareSpecsOptions & CompareSpecsOptions.IsMainnet) != 0;
        bool checkDifficultyBomb = (compareSpecsOptions & CompareSpecsOptions.CheckDifficultyBomb) != 0;
        bool isGnosis = (compareSpecsOptions & CompareSpecsOptions.IsGnosis) != 0;

        PropertyInfo[] propertyInfos =
            typeof(IReleaseSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (PropertyInfo propertyInfo in propertyInfos
                     .Where(p => p.Name != nameof(IReleaseSpec.Name))

                     // handle mainnet specific exceptions
                     .Where(p => isMainnet || p.Name != nameof(IReleaseSpec.MaximumExtraDataSize))
                     .Where(p => isMainnet || p.Name != nameof(IReleaseSpec.BlockReward))
                     .Where(p => isMainnet || checkDifficultyBomb ||
                                 p.Name != nameof(IReleaseSpec.DifficultyBombDelay))
                     .Where(p => isMainnet || checkDifficultyBomb ||
                                 p.Name != nameof(IReleaseSpec.DifficultyBoundDivisor))

                     // handle RLP decoders
                     .Where(p => p.Name != nameof(IReleaseSpec.Eip1559TransitionBlock))
                     .Where(p => p.Name != nameof(IReleaseSpec.WithdrawalTimestamp))
                     .Where(p => p.Name != nameof(IReleaseSpec.Eip4844TransitionTimestamp))

                     // Skip EIP-4844 parameter validation
                     .Where(p => p.Name != nameof(Eip4844Constants.BlobGasPriceUpdateFraction))
                     .Where(p => p.Name != nameof(Eip4844Constants.MaxBlobGasPerBlock))
                     .Where(p => p.Name != nameof(Eip4844Constants.MinBlobGasPrice))
                     .Where(p => p.Name != nameof(Eip4844Constants.TargetBlobGasPerBlock))

                     // handle gnosis specific exceptions
                     .Where(p => !isGnosis || p.Name != nameof(IReleaseSpec.MaxCodeSize))
                     .Where(p => !isGnosis || p.Name != nameof(IReleaseSpec.MaxInitCodeSize))
                     .Where(p => !isGnosis || p.Name != nameof(IReleaseSpec.MaximumUncleCount))
                     .Where(p => !isGnosis || p.Name != nameof(IReleaseSpec.IsEip170Enabled))
                     .Where(p => !isGnosis || p.Name != nameof(IReleaseSpec.IsEip1283Enabled))
                     .Where(p => !isGnosis || p.Name != nameof(IReleaseSpec.LimitCodeSize))
                     .Where(p => !isGnosis || p.Name != nameof(IReleaseSpec.UseConstantinopleNetGasMetering)))
        {
            Assert.That(propertyInfo.GetValue(actualSpec), Is.EqualTo(propertyInfo.GetValue(expectedSpec)),
                activation + "." + propertyInfo.Name);
        }
    }

    private ChainSpec LoadChainSpecFromChainFolder(string chain)
    {
        ChainSpecLoader loader = new(new EthereumJsonSerializer());
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"../../../../Chains/{chain}.json");
        return loader.Load(File.ReadAllText(path));
    }

    [Test]
    public void Chain_id_is_set_correctly()
    {
        ChainSpec chainSpec = new() { Parameters = new ChainParameters(), NetworkId = 2, ChainId = 5 };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.NetworkId, Is.EqualTo(2));
        Assert.That(provider.ChainId, Is.EqualTo(5));
    }

    [Test]
    public void Dao_block_number_is_set_correctly()
    {
        ChainSpec chainSpec = new();
        chainSpec.Parameters = new ChainParameters();
        chainSpec.DaoForkBlockNumber = 23;

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.DaoBlockNumber, Is.EqualTo(23));
    }

    [Test]
    public void Bound_divisors_set_correctly()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters { GasLimitBoundDivisor = 17 },
            Ethash = new EthashParameters { DifficultyBoundDivisor = 19 }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.GenesisSpec.DifficultyBoundDivisor, Is.EqualTo(19));
        Assert.That(provider.GenesisSpec.GasLimitBoundDivisor, Is.EqualTo(17));
    }

    [Test]
    public void Difficulty_bomb_delays_loaded_correctly()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Ethash = new EthashParameters
            {
                DifficultyBombDelays = new Dictionary<long, long>
                {
                    { 3, 100 },
                    { 7, 200 },
                    { 13, 300 },
                    { 17, 400 },
                    { 19, 500 },
                }
            }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.GetSpec((ForkActivation)3).DifficultyBombDelay, Is.EqualTo(100));
        Assert.That(provider.GetSpec((ForkActivation)7).DifficultyBombDelay, Is.EqualTo(300));
        Assert.That(provider.GetSpec((ForkActivation)13).DifficultyBombDelay, Is.EqualTo(600));
        Assert.That(provider.GetSpec((ForkActivation)17).DifficultyBombDelay, Is.EqualTo(1000));
        Assert.That(provider.GetSpec((ForkActivation)19).DifficultyBombDelay, Is.EqualTo(1500));
    }

    [Test]
    public void Max_code_transition_loaded_correctly()
    {
        const long maxCodeTransition = 13;
        const long maxCodeSize = 100;

        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters
            {
                MaxCodeSizeTransition = maxCodeTransition,
                MaxCodeSize = maxCodeSize
            }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.GetSpec((ForkActivation)(maxCodeTransition - 1)).MaxCodeSize, Is.EqualTo(long.MaxValue), "one before");
        Assert.That(provider.GetSpec((ForkActivation)maxCodeTransition).MaxCodeSize, Is.EqualTo(maxCodeSize), "at transition");
        Assert.That(provider.GetSpec((ForkActivation)(maxCodeTransition + 1)).MaxCodeSize, Is.EqualTo(maxCodeSize), "one after");
    }

    [Test]
    public void Eip2200_is_set_correctly_directly()
    {
        ChainSpec chainSpec = new() { Parameters = new ChainParameters { Eip2200Transition = 5 } };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeTrue();
    }

    [Test]
    public void Eip2200_is_set_correctly_indirectly()
    {
        ChainSpec chainSpec =
            new() { Parameters = new ChainParameters { Eip1706Transition = 5, Eip1283Transition = 5 } };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeTrue();
    }

    [Test]
    public void Eip2200_is_set_correctly_indirectly_after_disabling_eip1283_and_reenabling()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters
            {
                Eip1706Transition = 5,
                Eip1283Transition = 1,
                Eip1283DisableTransition = 4,
                Eip1283ReenableTransition = 5
            }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeTrue();
    }

    [Test]
    public void Eip2200_is_not_set_correctly_indirectly_after_disabling_eip1283()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters
            {
                Eip1706Transition = 5,
                Eip1283Transition = 1,
                Eip1283DisableTransition = 4
            }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeFalse();
    }

    [Test]
    public void Eip150_and_Eip2537_fork_by_block_number()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters
            {
                MaxCodeSizeTransition = 10,
                Eip2537Transition = 20,
                MaxCodeSize = 1
            }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);

        provider.GetSpec((ForkActivation)9).IsEip170Enabled.Should().BeFalse();
        provider.GetSpec((ForkActivation)10).IsEip170Enabled.Should().BeTrue();
        provider.GetSpec((ForkActivation)11).IsEip170Enabled.Should().BeTrue();
        provider.GetSpec((ForkActivation)11).MaxCodeSize.Should().Be(1);
        provider.GetSpec((ForkActivation)9).MaxCodeSize.Should().Be(long.MaxValue);

        provider.GetSpec((ForkActivation)19).IsEip2537Enabled.Should().BeFalse();
        provider.GetSpec((ForkActivation)20).IsEip2537Enabled.Should().BeTrue();
        provider.GetSpec((ForkActivation)21).IsEip2537Enabled.Should().BeTrue();
    }

    [Test]
    public void Eip150_and_Eip2537_fork_by_timestamp()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters
            {
                MaxCodeSizeTransitionTimestamp = 10,
                Eip2537TransitionTimestamp = 20,
                MaxCodeSize = 1
            }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);

        provider.GetSpec((100, 9)).IsEip170Enabled.Should().BeFalse();
        provider.GetSpec((100, 10)).IsEip170Enabled.Should().BeTrue();
        provider.GetSpec((100, 11)).IsEip170Enabled.Should().BeTrue();
        provider.GetSpec((100, 11)).MaxCodeSize.Should().Be(1);
        provider.GetSpec((100, 9)).MaxCodeSize.Should().Be(long.MaxValue);

        provider.GetSpec((100, 19)).IsEip2537Enabled.Should().BeFalse();
        provider.GetSpec((100, 20)).IsEip2537Enabled.Should().BeTrue();
        provider.GetSpec((100, 21)).IsEip2537Enabled.Should().BeTrue();
    }

    [Test]
    public void Eip_transitions_loaded_correctly()
    {
        const long maxCodeTransition = 1;
        const long maxCodeSize = 1;

        ChainSpec chainSpec = new()
        {
            Ethash =
                new EthashParameters
                {
                    HomesteadTransition = 70,
                    Eip100bTransition = 1000
                },
            ByzantiumBlockNumber = 1960,
            ConstantinopleBlockNumber = 6490,
            Parameters = new ChainParameters
            {
                MaxCodeSizeTransition = maxCodeTransition,
                MaxCodeSize = maxCodeSize,
                Registrar = Address.Zero,
                MinGasLimit = 11,
                GasLimitBoundDivisor = 13,
                MaximumExtraDataSize = 17,
                Eip140Transition = 1400L,
                Eip145Transition = 1450L,
                Eip150Transition = 1500L,
                Eip152Transition = 1520L,
                Eip155Transition = 1550L,
                Eip160Transition = 1600L,
                Eip161abcTransition = 1580L,
                Eip161dTransition = 1580L,
                Eip211Transition = 2110L,
                Eip214Transition = 2140L,
                Eip658Transition = 6580L,
                Eip1014Transition = 10140L,
                Eip1052Transition = 10520L,
                Eip1108Transition = 11080L,
                Eip1283Transition = 12830L,
                Eip1283DisableTransition = 12831L,
                Eip1344Transition = 13440L,
                Eip1884Transition = 18840L,
                Eip2028Transition = 20280L,
                Eip2200Transition = 22000L,
                Eip2315Transition = 23150L,
                Eip2565Transition = 25650L,
                Eip2929Transition = 29290L,
                Eip2930Transition = 29300L,
                Eip1559Transition = 15590L,
                Eip1559FeeCollectorTransition = 15591L,
                Eip1559FeeCollector = Address.SystemUser,
                Eip1559BaseFeeMinValueTransition = 15592L,
                Eip1559BaseFeeMinValue = UInt256.UInt128MaxValue,
                Eip3198Transition = 31980L,
                Eip3529Transition = 35290L,
                Eip3541Transition = 35410L,
                Eip1283ReenableTransition = 23000L,
                ValidateChainIdTransition = 24000L,
                ValidateReceiptsTransition = 24000L,
                MergeForkIdTransition = 40000L,
                Eip3651TransitionTimestamp = 1000000012,
                Eip3855TransitionTimestamp = 1000000012,
                Eip3860TransitionTimestamp = 1000000012,
                Eip1153TransitionTimestamp = 1000000024,
                Eip2537TransitionTimestamp = 1000000024
            }
        };

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.GetSpec((ForkActivation)(maxCodeTransition - 1)).MaxCodeSize, Is.EqualTo(long.MaxValue), "one before");
        Assert.That(provider.GetSpec((ForkActivation)maxCodeTransition).MaxCodeSize, Is.EqualTo(maxCodeSize), "at transition");
        Assert.That(provider.GetSpec((ForkActivation)(maxCodeTransition + 1)).MaxCodeSize, Is.EqualTo(maxCodeSize), "one after");

        ReleaseSpec expected = new();

        void TestTransitions(ForkActivation activation, Action<ReleaseSpec> changes)
        {
            changes(expected);
            IReleaseSpec underTest = provider.GetSpec(activation);
            underTest.Should().BeEquivalentTo(expected);
        }

        TestTransitions((ForkActivation)0L, r =>
        {
            r.MinGasLimit = 11L;
            r.GasLimitBoundDivisor = 13L;
            r.MaximumExtraDataSize = 17L;
            r.MaxCodeSize = long.MaxValue;
            r.Eip1559TransitionBlock = 15590L;
            r.IsTimeAdjustmentPostOlympic = true;
            r.MaximumUncleCount = 2;
            r.WithdrawalTimestamp = ulong.MaxValue;
            r.Eip4844TransitionTimestamp = ulong.MaxValue;
        });

        TestTransitions((ForkActivation)1L, r =>
        {
            r.MaxCodeSize = maxCodeSize;
            r.IsEip170Enabled = true;
        });
        TestTransitions((ForkActivation)70L, r => { r.IsEip2Enabled = r.IsEip7Enabled = true; });
        TestTransitions((ForkActivation)1000L, r => { r.IsEip100Enabled = true; });
        TestTransitions((ForkActivation)1400L, r => { r.IsEip140Enabled = true; });
        TestTransitions((ForkActivation)1450L, r => { r.IsEip145Enabled = true; });
        TestTransitions((ForkActivation)1500L, r => { r.IsEip150Enabled = true; });
        TestTransitions((ForkActivation)1520L, r => { r.IsEip152Enabled = true; });
        TestTransitions((ForkActivation)1550L, r => { r.IsEip155Enabled = true; });
        TestTransitions((ForkActivation)1580L, r => { r.IsEip158Enabled = true; });
        TestTransitions((ForkActivation)1600L, r => { r.IsEip160Enabled = true; });
        TestTransitions((ForkActivation)1960L,
            r => { r.IsEip196Enabled = r.IsEip197Enabled = r.IsEip198Enabled = r.IsEip649Enabled = true; });
        TestTransitions((ForkActivation)2110L, r => { r.IsEip211Enabled = true; });
        TestTransitions((ForkActivation)2140L, r => { r.IsEip214Enabled = true; });
        TestTransitions((ForkActivation)6580L, r => { r.IsEip658Enabled = r.IsEip1234Enabled = true; });
        TestTransitions((ForkActivation)10140L, r => { r.IsEip1014Enabled = true; });
        TestTransitions((ForkActivation)10520L, r => { r.IsEip1052Enabled = true; });
        TestTransitions((ForkActivation)11180L, r => { r.IsEip1108Enabled = true; });
        TestTransitions((ForkActivation)12830L, r => { r.IsEip1283Enabled = true; });
        TestTransitions((ForkActivation)12831L, r => { r.IsEip1283Enabled = false; });
        TestTransitions((ForkActivation)13440L, r => { r.IsEip1344Enabled = true; });
        TestTransitions((ForkActivation)15590L, r => { r.IsEip1559Enabled = true; });
        TestTransitions((ForkActivation)15591L, r => { r.Eip1559FeeCollector = Address.SystemUser; });
        TestTransitions((ForkActivation)15592L, r => { r.Eip1559BaseFeeMinValue = UInt256.UInt128MaxValue; });
        TestTransitions((ForkActivation)18840L, r => { r.IsEip1884Enabled = true; });
        TestTransitions((ForkActivation)20280L, r => { r.IsEip2028Enabled = true; });
        TestTransitions((ForkActivation)22000L, r => { r.IsEip2200Enabled = true; });
        TestTransitions((ForkActivation)23000L, r => { r.IsEip1283Enabled = r.IsEip1344Enabled = true; });
        TestTransitions((ForkActivation)24000L, r => { r.IsEip2315Enabled = r.ValidateChainId = r.ValidateReceipts = true; });
        TestTransitions((ForkActivation)29290L, r => { r.IsEip2929Enabled = r.IsEip2565Enabled = true; });
        TestTransitions((ForkActivation)29300L, r => { r.IsEip2930Enabled = true; });
        TestTransitions((ForkActivation)31980L, r => { r.IsEip3198Enabled = true; });
        TestTransitions((ForkActivation)35290L, r => { r.IsEip3529Enabled = true; });
        TestTransitions((ForkActivation)35410L, r => { r.IsEip3541Enabled = true; });
        TestTransitions((ForkActivation)35410L, r => { r.IsEip3541Enabled = true; });


        TestTransitions((41000L, 1000000012), r =>
        {
            r.IsEip3651Enabled = true;
            r.IsEip3855Enabled = true;
            r.IsEip3860Enabled = true;
        });
        TestTransitions((40001L, 1000000024), r => { r.IsEip1153Enabled = r.IsEip2537Enabled = true; });
    }

    [TestCaseSource(nameof(BlockNumbersAndTimestampsNearForkActivations))]
    public void Forks_should_be_selected_properly_for_exact_matches(ForkActivation forkActivation, bool isEip3651Enabled, bool isEip3198Enabled, bool isEip3855Enabled)
    {
        ISpecProvider provider = new CustomSpecProvider(
            (new ForkActivation(0), new ReleaseSpec() { IsEip3651Enabled = true }),
            (new ForkActivation(2, 10), new ReleaseSpec() { IsEip3651Enabled = true, IsEip3198Enabled = true, }),
            (new ForkActivation(2, 20), new ReleaseSpec() { IsEip3651Enabled = true, IsEip3198Enabled = true, IsEip3855Enabled = true })
            );

        IReleaseSpec spec = provider.GetSpec(forkActivation);
        Assert.Multiple(() =>
        {
            Assert.That(spec.IsEip3651Enabled, Is.EqualTo(isEip3651Enabled));
            Assert.That(spec.IsEip3198Enabled, Is.EqualTo(isEip3198Enabled));
            Assert.That(spec.IsEip3855Enabled, Is.EqualTo(isEip3855Enabled));
        });
    }

    public static IEnumerable BlockNumbersAndTimestampsNearForkActivations
    {
        get
        {
            yield return new TestCaseData(new ForkActivation(1), true, false, false);
            yield return new TestCaseData(new ForkActivation(2), true, false, false);
            yield return new TestCaseData(new ForkActivation(3), true, false, false);
            yield return new TestCaseData(new ForkActivation(1, 9), true, false, false);
            yield return new TestCaseData(new ForkActivation(2, 9), true, false, false);
            yield return new TestCaseData(new ForkActivation(2, 10), true, true, false);
            yield return new TestCaseData(new ForkActivation(2, 11), true, true, false);
            yield return new TestCaseData(new ForkActivation(2, 19), true, true, false);
            yield return new TestCaseData(new ForkActivation(2, 20), true, true, true);
            yield return new TestCaseData(new ForkActivation(2, 21), true, true, true);
            yield return new TestCaseData(new ForkActivation(3, 10), true, true, false);
            yield return new TestCaseData(new ForkActivation(3, 11), true, true, false);
            yield return new TestCaseData(new ForkActivation(3, 19), true, true, false);
            yield return new TestCaseData(new ForkActivation(3, 20), true, true, true);
            yield return new TestCaseData(new ForkActivation(3, 21), true, true, true);
        }
    }

    private static IEnumerable<ulong> GetTransitionTimestamps(ChainParameters parameters) => parameters.GetType()
        .Properties()
        .Where(p => p.Name.EndsWith("TransitionTimestamp", StringComparison.Ordinal))
        .Select(p => (ulong?)p.GetValue(parameters))
        .Where(t => t is not null)
        .Select(t => t!.Value);

    /// <summary>
    /// Validates the timestamp specified by making sure the resulting slot is a multiple of 8192.
    /// </summary>
    /// <param name="timestamp">The timestamp to validate</param>
    /// <param name="genesisTimestamp">The network's genesis timestamp</param>
    /// <param name="blockTime">The network's block time in seconds</param>
    /// <returns><c>true</c> if the timestamp is valid; otherwise, <c>false</c>.</returns>
    private static bool ValidateSlotByTimestamp(ulong timestamp, ulong genesisTimestamp, double blockTime = 12) =>
        timestamp > genesisTimestamp &&
        Math.Round((timestamp - genesisTimestamp) / blockTime) % 0x2000 == 0 &&
        Math.Ceiling((timestamp - genesisTimestamp) / blockTime) % 0x2000 == 0;
}
