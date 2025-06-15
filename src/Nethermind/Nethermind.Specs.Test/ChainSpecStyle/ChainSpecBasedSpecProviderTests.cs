// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[NonParallelizable]
[TestFixture]
public class ChainSpecBasedSpecProviderTests
{
    private const double GnosisBlockTime = 5;

    [SetUp]
    public void Setup()
    {
        Eip4844Constants.OverrideIfAny(1);
    }

    [TestCase(0, null, false)]
    [TestCase(0, 0ul, false)]
    [TestCase(0, 4660ul, false)]
    [TestCase(1, 4660ul, false)]
    [TestCase(1, 4661ul, false)]
    [TestCase(4, 4672ul, true)]
    [TestCase(4, 4673ul, true)]
    [TestCase(5, 4680ul, true)]
    [NonParallelizable]
    public void Timestamp_activation_equal_to_genesis_timestamp_loads_correctly(long blockNumber, ulong? timestamp, bool isEip3855Enabled)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"../../../../{Assembly.GetExecutingAssembly().GetName().Name}/Specs/Timestamp_activation_equal_to_genesis_timestamp_test.json");
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);
        chainSpec.Parameters.Eip2537Transition.Should().BeNull();
        ILogger logger = new(Substitute.ForPartsOf<LimboTraceLogger>());
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

    [Test]
    public void Missing_dependent_property()
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"../../../../{Assembly.GetExecutingAssembly().GetName().Name}/Specs/holesky_missing_deposit_contract.json");
        InvalidDataException? exception = Assert.Throws<InvalidDataException>(() => loader.LoadEmbeddedOrFromFile(path));
        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.Not.Null);
            Assert.That(exception.InnerException, Is.TypeOf<InvalidConfigurationException>());
            Assert.That(((InvalidConfigurationException?)exception.InnerException)?.ExitCode, Is.EqualTo(ExitCodes.MissingChainspecEipConfiguration));
        });
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
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"../../../../{Assembly.GetExecutingAssembly().GetName().Name}/Specs/Logs_warning_when_timestampActivation_happens_before_blockActivation_test.json");
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);
        chainSpec.Parameters.Eip2537Transition.Should().BeNull();
        InterfaceLogger iLogger = Substitute.For<InterfaceLogger>();
        iLogger.IsWarn.Returns(true);
        ILogger logger = new(iLogger);
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
            iLogger.Received(1).Warn(Arg.Is("Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition."));
        }
        else
        {
            iLogger.DidNotReceive().Warn(Arg.Is("Chainspec file is misconfigured! Timestamp transition is configured to happen before the last block transition."));
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
            yield return new TestCaseData(new ForkActivation(1735372, SepoliaSpecProvider.CancunTimestamp - 1)) { TestName = "Before Cancun" };
            yield return new TestCaseData(new ForkActivation(1735372, SepoliaSpecProvider.CancunTimestamp)) { TestName = "First Cancun" };
            yield return new TestCaseData(new ForkActivation(1735372, SepoliaSpecProvider.CancunTimestamp + 100000000)) { TestName = "Cancun" };
            yield return new TestCaseData(new ForkActivation(1735372, SepoliaSpecProvider.PragueTimestamp)) { TestName = "First Prague" };
            yield return new TestCaseData(new ForkActivation(1735372, SepoliaSpecProvider.PragueTimestamp + 100000000)) { TestName = "Prague" };
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
            static t => ValidateSlotByTimestamp(t, SepoliaSpecProvider.BeaconChainGenesisTimestampConst).Should().BeTrue());
        IReleaseSpec postCancunSpec = provider.GetSpec((2, SepoliaSpecProvider.CancunTimestamp));

        VerifyCancunSpecificsForMainnetAndHoleskyAndSepolia(postCancunSpec);

        IReleaseSpec postPragueSpec = provider.GetSpec((2, SepoliaSpecProvider.PragueTimestamp));
        VerifyPragueSpecificsForMainnetHoleskyHoodiAndSepolia(provider.ChainId, postPragueSpec);
    }

    public static IEnumerable<TestCaseData> HoleskyActivations
    {
        get
        {
            yield return new TestCaseData(new ForkActivation(0, HoleskySpecProvider.GenesisTimestamp)) { TestName = "Genesis" };
            yield return new TestCaseData(new ForkActivation(1, HoleskySpecProvider.ShanghaiTimestamp)) { TestName = "Shanghai" };
            yield return new TestCaseData(new ForkActivation(3, HoleskySpecProvider.ShanghaiTimestamp + 24)) { TestName = "Post Shanghai" };
            yield return new TestCaseData(new ForkActivation(4, HoleskySpecProvider.CancunTimestamp - 1)) { TestName = "Before Cancun" };
            yield return new TestCaseData(new ForkActivation(5, HoleskySpecProvider.CancunTimestamp)) { TestName = "Cancun" };
            yield return new TestCaseData(new ForkActivation(6, HoleskySpecProvider.CancunTimestamp + 24)) { TestName = "Post Cancun" };
            yield return new TestCaseData(new ForkActivation(7, HoleskySpecProvider.PragueTimestamp - 1)) { TestName = "Before Prague" };
            yield return new TestCaseData(new ForkActivation(8, HoleskySpecProvider.PragueTimestamp)) { TestName = "Prague" };
            yield return new TestCaseData(new ForkActivation(9, HoleskySpecProvider.PragueTimestamp + 100000000)) { TestName = "Future Prague" };
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

        IReleaseSpec postCancunSpec = provider.GetSpec((2, HoleskySpecProvider.CancunTimestamp));
        VerifyCancunSpecificsForMainnetAndHoleskyAndSepolia(postCancunSpec);
        IReleaseSpec postPragueSpec = provider.GetSpec((2, HoleskySpecProvider.PragueTimestamp));
        VerifyPragueSpecificsForMainnetHoleskyHoodiAndSepolia(provider.ChainId, postPragueSpec);

        // because genesis time for holesky is set 5 minutes before the launch of the chain. this test fails.
        //GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
        //    t => ValidateSlotByTimestamp(t, HoleskySpecProvider.GenesisTimestamp).Should().BeTrue());
    }

    private static void VerifyCancunSpecificsForMainnetAndHoleskyAndSepolia(IReleaseSpec spec)
    {
        Assert.Multiple(() =>
        {
            Assert.That(spec.BlobBaseFeeUpdateFraction, Is.EqualTo((UInt256)3338477));
            Assert.That(spec.GetMaxBlobGasPerBlock(), Is.EqualTo(786432));
            Assert.That(Eip4844Constants.MinBlobGasPrice, Is.EqualTo(1.Wei()));
            Assert.That(spec.GetTargetBlobGasPerBlock(), Is.EqualTo(393216));
        });
    }

    private static void VerifyPragueSpecificsForMainnetHoleskyHoodiAndSepolia(ulong chainId, IReleaseSpec spec)
    {
        Assert.Multiple(() =>
        {
            Assert.That(spec.BlobBaseFeeUpdateFraction, Is.EqualTo((UInt256)5007716));
            Assert.That(spec.MaxBlobCount, Is.EqualTo(9));
            Assert.That(spec.TargetBlobCount, Is.EqualTo(6));
            Assert.That(spec.Eip2935ContractAddress, Is.EqualTo(Eip2935Constants.BlockHashHistoryAddress));
        });

        Address expectedDepositContractAddress;
        switch (chainId)
        {
            case BlockchainIds.Mainnet:
                expectedDepositContractAddress = Eip6110Constants.MainnetDepositContractAddress;
                break;
            case BlockchainIds.Holesky:
                expectedDepositContractAddress = Eip6110Constants.HoleskyDepositContractAddress;
                break;
            case BlockchainIds.Hoodi:
                expectedDepositContractAddress = Eip6110Constants.HoodiDepositContractAddress;
                break;
            case BlockchainIds.Sepolia:
                expectedDepositContractAddress = Eip6110Constants.SepoliaDepositContractAddress;
                break;
            default:
                Assert.Fail("Unrecognised chain id when verifying Prague specifics.");
                return;
        }

        Assert.That(spec.DepositContractAddress, Is.EqualTo(expectedDepositContractAddress));
    }

    public static IEnumerable<TestCaseData> HoodiActivations
    {
        get
        {
            yield return new TestCaseData(new ForkActivation(0, HoodiSpecProvider.GenesisTimestamp)) { TestName = "Genesis" };
            yield return new TestCaseData(new ForkActivation(1, HoodiSpecProvider.ShanghaiTimestamp)) { TestName = "Shanghai" };
            yield return new TestCaseData(new ForkActivation(3, HoodiSpecProvider.ShanghaiTimestamp)) { TestName = "Post Shanghai" };
            yield return new TestCaseData(new ForkActivation(4, HoodiSpecProvider.CancunTimestamp)) { TestName = "Before Cancun" };
            yield return new TestCaseData(new ForkActivation(5, HoodiSpecProvider.CancunTimestamp)) { TestName = "Cancun" };
            yield return new TestCaseData(new ForkActivation(6, HoodiSpecProvider.CancunTimestamp)) { TestName = "Post Cancun" };
            yield return new TestCaseData(new ForkActivation(7, HoodiSpecProvider.PragueTimestamp - 1)) { TestName = "Before Prague" };
            yield return new TestCaseData(new ForkActivation(8, HoodiSpecProvider.PragueTimestamp)) { TestName = "Prague" };
            yield return new TestCaseData(new ForkActivation(9, HoodiSpecProvider.PragueTimestamp + 100000000)) { TestName = "Future Prague" };
        }
    }

    [TestCaseSource(nameof(HoodiActivations))]
    public void Hoodi_loads_properly(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("hoodi");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        ISpecProvider hardCodedSpec = HoodiSpecProvider.Instance;

        CompareSpecs(hardCodedSpec, provider, forkActivation);
        Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(hardCodedSpec.TerminalTotalDifficulty));
        Assert.That(provider.GenesisSpec.Eip1559TransitionBlock, Is.EqualTo(0));
        Assert.That(provider.GenesisSpec.DifficultyBombDelay, Is.EqualTo(0));
        Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Hoodi));
        Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Hoodi));

        IReleaseSpec postCancunSpec = provider.GetSpec((2, HoodiSpecProvider.CancunTimestamp));
        VerifyCancunSpecificsForMainnetAndHoleskyAndSepolia(postCancunSpec);
        IReleaseSpec postPragueSpec = provider.GetSpec((2, HoodiSpecProvider.PragueTimestamp));
        VerifyPragueSpecificsForMainnetHoleskyHoodiAndSepolia(provider.ChainId, postPragueSpec);
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
            yield return new TestCaseData((ForkActivation)(1, ChiadoSpecProvider.PragueTimestamp - 1)) { TestName = "Before Prague" };
            yield return new TestCaseData((ForkActivation)(1, ChiadoSpecProvider.PragueTimestamp)) { TestName = "Prague" };
            yield return new TestCaseData((ForkActivation)(1, ChiadoSpecProvider.PragueTimestamp + 100000000)) { TestName = "Future" };
        }
    }

    [TestCaseSource(nameof(ChiadoActivations))]
    public void Chiado_loads_properly(ForkActivation forkActivation)
    {
        // We need this to discover AuthorityRoundEngineParams
        new AuRaConfig();
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("chiado");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        ChiadoSpecProvider chiado = ChiadoSpecProvider.Instance;

        CompareSpecs(chiado, provider, forkActivation, CompareSpecsOptions.IsGnosis);
        Assert.Multiple(() =>
        {
            Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(ChiadoSpecProvider.Instance.TerminalTotalDifficulty));
            Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Chiado));
            Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Chiado));
        });

        IReleaseSpec? preShanghaiSpec = provider.GetSpec((1, ChiadoSpecProvider.ShanghaiTimestamp - 1));
        IReleaseSpec? postShanghaiSpec = provider.GetSpec((1, ChiadoSpecProvider.ShanghaiTimestamp));
        IReleaseSpec? postCancunSpec = provider.GetSpec((1, ChiadoSpecProvider.CancunTimestamp));
        IReleaseSpec? prePragueSpec = provider.GetSpec((1, ChiadoSpecProvider.PragueTimestamp - 1));
        IReleaseSpec? postPragueSpec = provider.GetSpec((1, ChiadoSpecProvider.PragueTimestamp));

        VerifyGnosisShanghaiSpecifics(preShanghaiSpec, postShanghaiSpec);
        VerifyGnosisCancunSpecifics(postCancunSpec);
        VerifyGnosisPragueSpecifics(prePragueSpec, postPragueSpec, ChiadoSpecProvider.FeeCollector);
        GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
            static t => ValidateSlotByTimestamp(t, ChiadoSpecProvider.BeaconChainGenesisTimestampConst, GnosisBlockTime).Should().BeTrue());
        Assert.That(postPragueSpec.DepositContractAddress, Is.EqualTo(new Address("0xb97036A26259B7147018913bD58a774cf91acf25")));
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
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber + 1, GnosisSpecProvider.ShanghaiTimestamp - 1))
            { TestName = "Before Shanghai" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber + 1, GnosisSpecProvider.ShanghaiTimestamp))
            { TestName = "Shanghai" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber + 2, GnosisSpecProvider.CancunTimestamp - 1))
            { TestName = "Before Cancun" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber + 2, GnosisSpecProvider.CancunTimestamp))
            { TestName = "Cancun" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber + 2, GnosisSpecProvider.CancunTimestamp + 100000000))
            { TestName = "Future" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber + 2, GnosisSpecProvider.PragueTimestamp - 1))
            { TestName = "Before Prague" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber + 2, GnosisSpecProvider.PragueTimestamp))
            { TestName = "Prague" };
            yield return new TestCaseData((ForkActivation)(GnosisSpecProvider.LondonBlockNumber + 2, GnosisSpecProvider.PragueTimestamp + 100000000))
            { TestName = "Future" };
        }
    }

    [TestCaseSource(nameof(GnosisActivations))]
    public void Gnosis_loads_properly(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadChainSpecFromChainFolder("gnosis");
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        GnosisSpecProvider gnosisSpecProvider = GnosisSpecProvider.Instance;

        CompareSpecs(gnosisSpecProvider, provider, forkActivation, CompareSpecsOptions.IsGnosis);
        Assert.Multiple(() =>
        {
            Assert.That(provider.TerminalTotalDifficulty, Is.EqualTo(GnosisSpecProvider.Instance.TerminalTotalDifficulty));
            Assert.That(provider.ChainId, Is.EqualTo(BlockchainIds.Gnosis));
            Assert.That(provider.NetworkId, Is.EqualTo(BlockchainIds.Gnosis));
        });

        VerifyGnosisPreShanghaiSpecifics(provider);

        IReleaseSpec? preShanghaiSpec = provider.GetSpec((1, GnosisSpecProvider.ShanghaiTimestamp - 1));
        IReleaseSpec? postShanghaiSpec = provider.GetSpec((1, GnosisSpecProvider.ShanghaiTimestamp));
        IReleaseSpec? postCancunSpec = provider.GetSpec((1, GnosisSpecProvider.CancunTimestamp));
        IReleaseSpec? prePragueSpec = provider.GetSpec((1, GnosisSpecProvider.PragueTimestamp - 1));
        IReleaseSpec? postPragueSpec = provider.GetSpec((1, GnosisSpecProvider.PragueTimestamp));

        VerifyGnosisShanghaiSpecifics(preShanghaiSpec, postShanghaiSpec);
        VerifyGnosisCancunSpecifics(postCancunSpec);
        VerifyGnosisPragueSpecifics(prePragueSpec, postPragueSpec, GnosisSpecProvider.FeeCollector);
        Assert.That(postPragueSpec.DepositContractAddress, Is.EqualTo(new Address("0x0B98057eA310F4d31F2a452B414647007d1645d9")));
        GetTransitionTimestamps(chainSpec.Parameters).Should().AllSatisfy(
            static t => ValidateSlotByTimestamp(t, GnosisSpecProvider.BeaconChainGenesisTimestampConst, GnosisBlockTime).Should().BeTrue());
    }

    private static void VerifyGnosisPragueSpecifics(IReleaseSpec prePragueSpec, IReleaseSpec postPragueSpec, Address feeCollector)
    {
        Assert.Multiple(() =>
        {
            Assert.That(prePragueSpec.FeeCollector, Is.EqualTo(feeCollector));
            Assert.That(postPragueSpec.FeeCollector, Is.EqualTo(feeCollector));
            Assert.That(prePragueSpec.IsEip4844FeeCollectorEnabled, Is.EqualTo(false));
            Assert.That(postPragueSpec.IsEip4844FeeCollectorEnabled, Is.EqualTo(true));
            Assert.That(postPragueSpec.Eip2935ContractAddress, Is.EqualTo(Eip2935Constants.BlockHashHistoryAddress));
        });

        // should be unchanged
        VerifyGnosisCancunSpecifics(postPragueSpec);
    }

    private static void VerifyGnosisCancunSpecifics(IReleaseSpec spec)
    {
        Assert.Multiple(() =>
        {
            Assert.That(spec.BlobBaseFeeUpdateFraction, Is.EqualTo((UInt256)1112826));
            Assert.That(spec.GetMaxBlobGasPerBlock(), Is.EqualTo(262144));
            Assert.That(Eip4844Constants.MinBlobGasPrice, Is.EqualTo(1.GWei()));
            Assert.That(spec.GetTargetBlobGasPerBlock(), Is.EqualTo(131072));
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
            yield return new TestCaseData(new ForkActivation(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp - 1)) { TestName = "Before Cancun" };
            yield return new TestCaseData(MainnetSpecProvider.CancunActivation) { TestName = "Cancun" };
            yield return new TestCaseData(new ForkActivation(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.PragueBlockTimestamp - 1)) { TestName = "Before Prague" };
            yield return new TestCaseData(MainnetSpecProvider.PragueActivation) { TestName = "Prague" };
            yield return new TestCaseData(new ForkActivation(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.PragueBlockTimestamp + 100000000)) { TestName = "Future" };
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
            static t => ValidateSlotByTimestamp(t, MainnetSpecProvider.BeaconChainGenesisTimestampConst).Should().BeTrue());
        IReleaseSpec postCancunSpec = provider.GetSpec(MainnetSpecProvider.CancunActivation);
        IReleaseSpec postPragueSpec = provider.GetSpec(MainnetSpecProvider.PragueActivation);

        VerifyCancunSpecificsForMainnetAndHoleskyAndSepolia(postCancunSpec);
        VerifyPragueSpecificsForMainnetHoleskyHoodiAndSepolia(provider.ChainId, postPragueSpec);
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
                     .Where(p => isMainnet || checkDifficultyBomb || p.Name != nameof(IReleaseSpec.DifficultyBombDelay))
                     .Where(p => isMainnet || checkDifficultyBomb || p.Name != nameof(IReleaseSpec.DifficultyBoundDivisor))
                     .Where(p => isMainnet || p.Name != nameof(IReleaseSpec.DepositContractAddress))

                     // handle RLP decoders
                     .Where(p => p.Name != nameof(IReleaseSpec.Eip1559TransitionBlock))
                     .Where(p => p.Name != nameof(IReleaseSpec.WithdrawalTimestamp))
                     .Where(p => p.Name != nameof(IReleaseSpec.Eip4844TransitionTimestamp))

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
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"../../../../Chains/{chain}.json");
        var chainSpec = loader.LoadEmbeddedOrFromFile(path);
        return chainSpec;
    }

    [Test]
    public void Chain_id_is_set_correctly()
    {
        ChainSpec chainSpec = new() { Parameters = new ChainParameters(), NetworkId = 2, ChainId = 5 };
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.NetworkId, Is.EqualTo(2));
        Assert.That(provider.ChainId, Is.EqualTo(5));
    }

    [Test]
    public void Dao_block_number_is_set_correctly()
    {
        ChainSpec chainSpec = new();
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;
        chainSpec.Parameters = new ChainParameters();
        chainSpec.DaoForkBlockNumber = 23;

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.DaoBlockNumber, Is.EqualTo(23));
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
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Assert.That(provider.GetSpec((ForkActivation)(maxCodeTransition - 1)).MaxCodeSize, Is.EqualTo(long.MaxValue), "one before");
        Assert.That(provider.GetSpec((ForkActivation)maxCodeTransition).MaxCodeSize, Is.EqualTo(maxCodeSize), "at transition");
        Assert.That(provider.GetSpec((ForkActivation)(maxCodeTransition + 1)).MaxCodeSize, Is.EqualTo(maxCodeSize), "one after");
    }

    [Test]
    public void Eip2200_is_set_correctly_directly()
    {
        ChainSpec chainSpec = new() { Parameters = new ChainParameters { Eip2200Transition = 5 } };
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        provider.GetSpec((ForkActivation)5).IsEip2200Enabled.Should().BeTrue();
    }

    [Test]
    public void Eip2200_is_set_correctly_indirectly()
    {
        ChainSpec chainSpec =
            new() { Parameters = new ChainParameters { Eip1706Transition = 5, Eip1283Transition = 5 } };
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;

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
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;

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
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;

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
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;

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
        chainSpec.EngineChainSpecParametersProvider = TestChainSpecParametersProvider.NethDev;

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

    [TestCaseSource(nameof(BlobScheduleActivationsTestCaseSource))]
    public void Test_BlobSchedule_IsApplied_AlongWithForkSchedule(
        ulong eip4844Timestamp,
        ulong eip7002Timestamp,
        BlobScheduleSettings[] blobScheduleSettings,
        ulong[] expectedActivationSettings)
    {
        (ChainSpecBasedSpecProvider provider, _) = TestSpecHelper.LoadChainSpec(new ChainSpecJson
        {
            Params = new ChainSpecParamsJson
            {
                Eip4844TransitionTimestamp = eip4844Timestamp,
                Eip7002TransitionTimestamp = eip7002Timestamp,
                BlobSchedule = [.. blobScheduleSettings]
            },
        });

        IReleaseSpec spec = provider.GenesisSpec;
        Assert.That(spec.MaxBlobCount, Is.EqualTo(expectedActivationSettings[0]));

        expectedActivationSettings = expectedActivationSettings[1..];
        Assert.That(expectedActivationSettings, Has.Length.EqualTo(provider.TransitionActivations.Length));

        for (int i = 0; i < expectedActivationSettings.Length; i++)
        {
            spec = provider.GetSpec(ForkActivation.TimestampOnly(provider.TransitionActivations[i].Timestamp!.Value));
            Assert.That(spec.MaxBlobCount, Is.EqualTo(expectedActivationSettings[i]));
        }
    }

    public static IEnumerable BlobScheduleActivationsTestCaseSource
    {
        get
        {
            const int NoneAllowed = 0;
            const int Default = 6;
            static TestCaseData MakeTestCase(string testName, int eip4844Timestamp, int eip7002Timestamp, (int timestamp, int max)[] settings, ulong[] expectedActivationSettings)
                => new([
                    (ulong)eip4844Timestamp,
                    (ulong)eip7002Timestamp,
                    settings.Select(s => new BlobScheduleSettings { Timestamp = (ulong)s.timestamp, Max = (ulong)s.max }).ToArray(),
                    expectedActivationSettings])
                { TestName = $"BlobScheduleActivations: {testName}" };

            yield return MakeTestCase("Default", 1, 2, [], [NoneAllowed, Default, Default]);

            yield return MakeTestCase("Both activate not at genesis", 1, 1, [], [NoneAllowed, Default]);

            yield return MakeTestCase("Named only from genesis", 0, 0, [], [Default]);

            yield return MakeTestCase("Default from genesis + BPO", 0, 0, [(1, 7)], [Default, 7]);

            yield return MakeTestCase("BPO from genesis", 0, 0, [(0, 7)], [7]);

            yield return MakeTestCase("A named fork has no change in settings", 1, 2, [(3, 10)], [NoneAllowed, Default, Default, 10]);

            yield return MakeTestCase("Cancun and Prague have default settings, but a between bpo changes it", 0, 2, [(1, 10)], [Default, 10, 10]);

            yield return MakeTestCase("Multiple BPOs", 0, 0, [
                (1, 5),
                (2, 6),
                (3, 10),
                (4, 12),
                (5, 10),
                (6, 10)],
                [Default, 5, 6, 10, 12, 10, 10]);

            yield return MakeTestCase("BPOs match named forks", 1, 2, [(1, 10), (2, 3)], [NoneAllowed, 10, 3]);

            yield return MakeTestCase("BPO timestamp matches genesis, but not any other fork", 0, 2, [(0, 10), (1, 11)], [10, 11, 11]);

            yield return MakeTestCase("Unordered", 0, 2, [(4, 10), (3, 11)], [Default, Default, 11, 10]);

            yield return MakeTestCase("Unordered between named forks", 0, 2, [(4, 10), (1, 11)], [Default, 11, 11, 10]);
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
