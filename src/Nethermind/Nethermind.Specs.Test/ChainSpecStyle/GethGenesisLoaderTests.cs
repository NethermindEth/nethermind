// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
public class GethGenesisLoaderTests
{
    /// <param name="TransitionSuffix">"TransitionTimestamp" for timestamp forks, "Transition" for block forks.</param>
    private readonly record struct ForkActivationInfo(NamedReleaseSpec Fork, NamedReleaseSpec Parent, string GethConfigName, long ActivationValue, string TransitionSuffix);

    // EIPs handled by Ethash engine params, precompiles, or non-standard property names
    private static readonly HashSet<string> EipsWithNonStandardMapping =
    [
        "2",    // Homestead — Ethash engine params
        "100",  // Byzantium — Ethash Eip100bTransition
        "158",  // SpuriousDragon — mapped via Eip161abcTransition/Eip161dTransition
        "170",  // SpuriousDragon — mapped via MaxCodeSizeTransition
        "196",  // Byzantium — precompile, no separate transition
        "197",  // Byzantium — precompile, no separate transition
        "198",  // Byzantium — precompile, no separate transition
        "649",  // Byzantium — difficulty bomb, Ethash engine
        "1234", // Constantinople — difficulty bomb, Ethash engine
    ];

    // Config properties whose name doesn't match any fork class (legacy aliases or special transitions)
    private static readonly HashSet<string> ConfigPropsWithoutForkClass =
    [
        "DaoForkBlock",       // fork class is "Dao", not "DaoFork"
        "Eip150Block",        // alias for TangerineWhistleBlock
        "Eip155Block",        // alias for SpuriousDragonBlock
        "Eip158Block",        // alias for SpuriousDragonBlock
        "PetersburgBlock",    // fork class is "ConstantinopleFix"
        "MergeNetsplitBlock", // fork ID transition, not a fork class
    ];

    private static readonly string[] AmsterdamEipNumbers = ["7708", "7778", "7843", "7928", "7954", "8024", "8037"];

    private static ChainSpec LoadChainSpec(string path) =>
        new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(path);

    private static ChainSpec LoadFromString(string json)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return new GethGenesisLoader(new EthereumJsonSerializer()).Load(stream);
    }

    private static ChainSpec LoadAutoDetecting(string json)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return new AutoDetectingChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(stream);
    }

    private static ChainSpec LoadHoodiChainSpec() => LoadChainSpec(
        Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/hoodi.json"));

    /// <summary>
    /// Builds a standard geth genesis JSON with homesteadBlock/eip150Block/eip155Block/eip158Block all set to 0.
    /// </summary>
    private static string BuildStandardGethGenesisJson(
        int chainId = 1,
        string configExtra = "",
        string allocJson = "{}",
        ulong? timestamp = null)
    {
        string configSuffix = configExtra.Length > 0 ? ", " + configExtra : "";
        string timestampField = timestamp.HasValue ? $"\"timestamp\": {timestamp.Value}, " : "";
        return $$"""
            {
              "config": {
                "chainId": {{chainId}},
                "homesteadBlock": 0,
                "eip150Block": 0,
                "eip155Block": 0,
                "eip158Block": 0{{configSuffix}}
              },
              {{timestampField}}"difficulty": "0x1",
              "gasLimit": "0x8000000",
              "alloc": {{allocJson}}
            }
            """;
    }

    private static ChainSpec LoadStandardGethGenesis(
        int chainId = 1,
        string configExtra = "",
        string allocJson = "{}",
        ulong? timestamp = null) =>
        LoadFromString(BuildStandardGethGenesisJson(chainId, configExtra, allocJson, timestamp));

    private static void AssertAmsterdamEipsEnabled(IReleaseSpec spec, bool expected)
    {
        foreach (string eip in AmsterdamEipNumbers)
        {
            bool value = (bool)spec.GetType().GetProperty($"IsEip{eip}Enabled")!.GetValue(spec)!;
            value.Should().Be(expected, $"IsEip{eip}Enabled");
        }
    }

    private static void AssertAmsterdamTransitionTimestamps(ChainSpec chainSpec, ulong expectedTimestamp)
    {
        foreach (string eip in AmsterdamEipNumbers)
        {
            PropertyInfo property = chainSpec.Parameters.GetType().GetProperty($"Eip{eip}TransitionTimestamp")!;
            ulong? value = (ulong?)property.GetValue(chainSpec.Parameters);
            value.Should().Be(expectedTimestamp, $"Eip{eip}TransitionTimestamp");
        }
    }

    [Test]
    public void Can_load_hoodi_eip7949()
    {
        ChainSpec chainSpec = LoadHoodiChainSpec();

        chainSpec.ChainId.Should().Be(560048);
        chainSpec.NetworkId.Should().Be(560048);

        chainSpec.TangerineWhistleBlockNumber.Should().Be(0);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(0);
        chainSpec.ByzantiumBlockNumber.Should().Be(0);
        chainSpec.ConstantinopleBlockNumber.Should().Be(0);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(0);
        chainSpec.IstanbulBlockNumber.Should().Be(0);
        chainSpec.BerlinBlockNumber.Should().Be(0);
        chainSpec.LondonBlockNumber.Should().Be(0);
        chainSpec.ShanghaiTimestamp.Should().Be(0);
        chainSpec.CancunTimestamp.Should().Be(0);
        chainSpec.PragueTimestamp.Should().Be(1742999832);
        chainSpec.OsakaTimestamp.Should().Be(1761677592);

        chainSpec.Genesis.Should().NotBeNull();
        chainSpec.Genesis.Header.GasLimit.Should().Be(0x2255100);

        chainSpec.Allocations.Should().NotBeEmpty();
        chainSpec.Allocations[Address.Zero].Balance.Should().Be(1);

        chainSpec.Parameters.BlobSchedule.Should().NotBeEmpty();
        chainSpec.Parameters.BlobSchedule.Should().HaveCount(3);

        chainSpec.Parameters.DepositContractAddress.Should().Be(new Address("0x00000000219ab540356cBB839Cbe05303d7705Fa"));
    }

    [Test]
    public void Can_load_minimal_geth_genesis()
    {
        ChainSpec chainSpec = LoadStandardGethGenesis(
            chainId: 12345,
            allocJson: """{ "0x0000000000000000000000000000000000000001": { "balance": "0x1" } }""");

        chainSpec.ChainId.Should().Be(12345);
        chainSpec.NetworkId.Should().Be(12345);
        chainSpec.SealEngineType.Should().Be(SealEngineType.Ethash);
        chainSpec.Genesis.Header.GasLimit.Should().Be(0x8000000);
        chainSpec.Genesis.Header.Difficulty.Should().Be(UInt256.One);
        chainSpec.Allocations.Should().HaveCount(1);
        chainSpec.Allocations[new Address("0x0000000000000000000000000000000000000001")].Balance.Should().Be(1);

        EthashChainSpecEngineParameters ethashParameters = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<EthashChainSpecEngineParameters>();
        ethashParameters.DifficultyBoundDivisor.Should().Be(0x800);
        ethashParameters.BlockReward.Should().ContainKey(0);

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        IReleaseSpec genesisSpec = provider.GetSpec(new ForkActivation(0));
        genesisSpec.DifficultyBoundDivisor.Should().Be(0x800);
        genesisSpec.BlockReward.Should().Be(new UInt256(5_000_000_000_000_000_000ul));
    }

    [Test]
    public void Can_load_genesis_with_timestamp_forks()
    {
        ChainSpec chainSpec = LoadStandardGethGenesis(configExtra: """
            "byzantiumBlock": 0,
            "constantinopleBlock": 0,
            "petersburgBlock": 0,
            "istanbulBlock": 0,
            "berlinBlock": 0,
            "londonBlock": 0,
            "terminalTotalDifficulty": "0x0",
            "shanghaiTime": 1681338455,
            "cancunTime": 1710338135,
            "pragueTime": 1800000000
            """);

        chainSpec.ChainId.Should().Be(1);
        chainSpec.ShanghaiTimestamp.Should().Be(1681338455);
        chainSpec.CancunTimestamp.Should().Be(1710338135);
        chainSpec.PragueTimestamp.Should().Be(1800000000);
        chainSpec.TerminalTotalDifficulty.Should().Be(UInt256.Zero);
    }

    [Test]
    public void Can_load_genesis_with_amsterdam_time()
    {
        ChainSpec chainSpec = LoadStandardGethGenesis(configExtra: "\"amsterdamTime\": 15");

        chainSpec.AmsterdamTimestamp.Should().Be(15);
        AssertAmsterdamTransitionTimestamps(chainSpec, 15);

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        AssertAmsterdamEipsEnabled(provider.GetSpec(ForkActivation.TimestampOnly(14)), false);

        IReleaseSpec amsterdam = provider.GetSpec(ForkActivation.TimestampOnly(15));
        AssertAmsterdamEipsEnabled(amsterdam, true);
        amsterdam.MaxCodeSize.Should().Be(CodeSizeConstants.MaxCodeSizeEip7954);

        // When genesis timestamp matches amsterdamTime, genesis header fields are set
        ChainSpec genesisAtAmsterdam = LoadStandardGethGenesis(configExtra: "\"amsterdamTime\": 15", timestamp: 15);
        ChainSpecBasedSpecProvider genesisProvider = new(genesisAtAmsterdam);

        genesisAtAmsterdam.Genesis.BlockAccessListHash.Should().Be(Keccak.OfAnEmptySequenceRlp);
        genesisAtAmsterdam.Genesis.SlotNumber.Should().Be(0);
        AssertAmsterdamEipsEnabled(genesisProvider.GenesisSpec, true);
        genesisProvider.GenesisSpec.MaxCodeSize.Should().Be(CodeSizeConstants.MaxCodeSizeEip7954);
    }

    [Test]
    public void Maps_additional_eips_to_standard_fork_timestamps()
    {
        const string genesis = """
        {
          "config": {
            "chainId": 1,
            "homesteadBlock": 1,
            "eip150Block": 2,
            "eip155Block": 3,
            "eip158Block": 4,
            "byzantiumBlock": 5,
            "constantinopleBlock": 6,
            "petersburgBlock": 7,
            "istanbulBlock": 8,
            "berlinBlock": 9,
            "londonBlock": 10,
            "terminalTotalDifficulty": "0x0",
            "shanghaiTime": 11,
            "cancunTime": 12,
            "pragueTime": 13,
            "osakaTime": 14
          },
          "difficulty": "0x1",
          "gasLimit": "0x8000000",
          "alloc": {}
        }
        """;

        ChainSpec chainSpec = LoadFromString(genesis);

        chainSpec.Parameters.Eip7Transition.Should().Be(1);
        chainSpec.Parameters.ValidateChainIdTransition.Should().Be(3);
        chainSpec.Parameters.ValidateReceiptsTransition.Should().Be(5);
    }

    [Test]
    public void Can_load_genesis_with_blob_schedule()
    {
        ChainSpec chainSpec = LoadStandardGethGenesis(configExtra: """
            "cancunTime": 1710338135,
            "pragueTime": 1800000000,
            "blobSchedule": {
                "cancun": { "target": 3, "max": 6, "baseFeeUpdateFraction": 3338477 },
                "prague": { "target": 6, "max": 9, "baseFeeUpdateFraction": 5007716 }
            }
            """);

        chainSpec.Parameters.BlobSchedule.Should().HaveCount(2);

        List<BlobScheduleSettings> blobScheduleList = [.. chainSpec.Parameters.BlobSchedule];
        blobScheduleList[0].Timestamp.Should().Be(1710338135);
        blobScheduleList[0].Target.Should().Be(3);
        blobScheduleList[0].Max.Should().Be(6);

        blobScheduleList[1].Timestamp.Should().Be(1800000000);
        blobScheduleList[1].Target.Should().Be(6);
        blobScheduleList[1].Max.Should().Be(9);
    }

    [Test]
    public void Can_load_genesis_with_account_storage_and_code()
    {
        ChainSpec chainSpec = LoadStandardGethGenesis(allocJson: """
            {
                "0x0000000000000000000000000000000000000100": {
                    "balance": "0xde0b6b3a7640000",
                    "nonce": "0x1",
                    "code": "0x6080604052",
                    "storage": {
                        "0x0000000000000000000000000000000000000000000000000000000000000001": "0x00000000000000000000000000000000000000000000000000000000000000ff"
                    }
                }
            }
            """);

        Address address = new("0x0000000000000000000000000000000000000100");
        chainSpec.Allocations.Should().ContainKey(address);

        ChainSpecAllocation allocation = chainSpec.Allocations[address];
        allocation.Balance.Should().Be(UInt256.Parse("1000000000000000000")); // 1 ETH in wei
        allocation.Nonce.Should().Be(1);
        allocation.Code.Should().BeEquivalentTo(new byte[] { 0x60, 0x80, 0x60, 0x40, 0x52 });
        allocation.Storage.Should().HaveCount(1);
    }

    [Test]
    public void Can_load_genesis_without_0x_prefix_in_addresses()
    {
        ChainSpec chainSpec = LoadStandardGethGenesis(allocJson: """
            {
                "0000000000000000000000000000000000000001": { "balance": "0x1" },
                "0x0000000000000000000000000000000000000002": { "balance": "0x2" }
            }
            """);

        chainSpec.Allocations.Should().HaveCount(2);
        chainSpec.Allocations[new Address("0x0000000000000000000000000000000000000001")].Balance.Should().Be(1);
        chainSpec.Allocations[new Address("0x0000000000000000000000000000000000000002")].Balance.Should().Be(2);
    }

    [Test]
    public void AutoDetectingLoader_detects_geth_format()
    {
        ChainSpec chainSpec = LoadAutoDetecting(BuildStandardGethGenesisJson(chainId: 12345));
        chainSpec.ChainId.Should().Be(12345);
    }

    [Test]
    public void AutoDetectingLoader_detects_parity_format()
    {
        const string parityChainspec = """
        {
          "name": "TestNet",
          "engine": { "Ethash": {} },
          "params": {
            "chainID": "0x1",
            "eip150Transition": "0x0"
          },
          "genesis": {
            "difficulty": "0x1",
            "gasLimit": "0x1388"
          },
          "accounts": {}
        }
        """;

        ChainSpec chainSpec = LoadAutoDetecting(parityChainspec);

        chainSpec.Name.Should().Be("TestNet");
        chainSpec.ChainId.Should().Be(1);
    }

    [Test]
    public void Can_load_genesis_with_deposit_contract()
    {
        ChainSpec chainSpec = LoadStandardGethGenesis(configExtra:
            "\"pragueTime\": 1800000000, " +
            "\"depositContractAddress\": \"0x00000000219ab540356cBB839Cbe05303d7705Fa\"");

        chainSpec.Parameters.DepositContractAddress.Should().Be(new Address("0x00000000219ab540356cBB839Cbe05303d7705Fa"));
        chainSpec.Parameters.Eip6110TransitionTimestamp.Should().Be(1800000000);
    }

    /// <summary>
    /// Returns EIP numbers newly enabled by <paramref name="fork"/> compared to its <paramref name="parent"/>.
    /// </summary>
    private static IEnumerable<string> GetNewlyEnabledEips(NamedReleaseSpec fork, NamedReleaseSpec parent)
    {
        foreach (PropertyInfo prop in fork.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            bool isEipActivation = prop.PropertyType == typeof(bool) && prop.Name.StartsWith("IsEip") && prop.Name.EndsWith("Enabled");
            if (isEipActivation)
            {
                bool value = (bool)prop.GetValue(fork)!;
                bool parentValue = (bool)prop.GetValue(parent)!;
                if (value && !parentValue)
                {
                    yield return prop.Name["IsEip".Length..^"Enabled".Length];
                }
            }
        }
    }

    /// <summary>
    /// Returns all concrete <see cref="NamedReleaseSpec"/> instances with their parents.
    /// </summary>
    private static IEnumerable<(Type type, NamedReleaseSpec instance)> GetAllForkInstances()
    {
        IEnumerable<Type> GetNameReleaseSpecs() => typeof(NamedReleaseSpec).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(NamedReleaseSpec))
                        && t is { IsAbstract: false, IsGenericType: false }
                        && t.Namespace == typeof(NamedReleaseSpec).Namespace);

        // Only mainnet forks — exclude network-specific variants (e.g. GnosisForks)
        foreach (Type type in GetNameReleaseSpecs())
        {
            PropertyInfo? instanceProp = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (instanceProp is not null)
            {
                NamedReleaseSpec instance = (NamedReleaseSpec)instanceProp.GetValue(null)!;
                yield return (type, instance);
            }
        }
    }

    /// <summary>
    /// Discovers all forks with matching activation properties on <see cref="GethGenesisConfigJson"/>:
    /// <c>{Name}Time</c> (ulong?) for timestamp forks, <c>{Name}Block</c> (long?) for block forks.
    /// </summary>
    private static List<ForkActivationInfo> DiscoverGethForks()
    {
        static (Type type, NamedReleaseSpec instance) FindFork((Type type, NamedReleaseSpec instance)[] forks, string name) =>
            forks.FirstOrDefault(f => f.type.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        Type configType = typeof(GethGenesisConfigJson);
        (Type type, NamedReleaseSpec instance)[] allForks = GetAllForkInstances().ToArray();
        List<ForkActivationInfo> result = [];
        long value = 1;

        foreach (PropertyInfo prop in configType.GetProperties())
        {
            bool isTime = prop.PropertyType == typeof(ulong?) && prop.Name.EndsWith("Time");
            bool isBlock = prop.PropertyType == typeof(long?) && prop.Name.EndsWith("Block");

            if (isTime || isBlock)
            {
                (string suffix, string transitionSuffix) = isTime ? ("Time", "TransitionTimestamp") : ("Block", "Transition");
                string forkName = prop.Name[..^suffix.Length];
                (Type type, NamedReleaseSpec instance) match = FindFork(allForks, forkName);
                if (match.instance?.Parent is not null)
                {
                    string gethConfigName = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
                    result.Add(new(match.instance, match.instance.Parent, gethConfigName, value, transitionSuffix));
                    value++;
                }
            }
        }

        return result;
    }

    [Test]
    public void GethGenesisConfigJson_and_fork_classes_are_in_sync()
    {
        Type configType = typeof(GethGenesisConfigJson);
        (Type type, NamedReleaseSpec instance)[] allForks = GetAllForkInstances().ToArray();
        List<string> mismatches = [];
        int configPropsChecked = 0;
        int forkClassesChecked = 0;

        // Every *Time and *Block property must have a matching fork class
        foreach (PropertyInfo prop in configType.GetProperties())
        {
            bool isTime = prop.PropertyType == typeof(ulong?) && prop.Name.EndsWith("Time");
            bool isBlock = prop.PropertyType == typeof(long?) && prop.Name.EndsWith("Block");

            if (isTime || isBlock)
            {
                configPropsChecked++;
                string suffix = isTime ? "Time" : "Block";
                if (!ConfigPropsWithoutForkClass.Contains(prop.Name))
                {
                    string forkName = prop.Name[..^suffix.Length];
                    if (!allForks.Any(f => f.type.Name.Equals(forkName, StringComparison.OrdinalIgnoreCase)))
                    {
                        mismatches.Add($"GethGenesisConfigJson.{prop.Name} has no matching fork class");
                    }
                }
            }
        }

        // Every fork class that introduces EIPs must have a *Time or *Block property
        foreach ((Type type, NamedReleaseSpec instance) in allForks)
        {
            if (instance.Parent is not null && GetNewlyEnabledEips(instance, instance.Parent).Any())
            {
                forkClassesChecked++;
                bool hasBlockProp = configType.GetProperties().Any(p => p.Name.Equals($"{type.Name}Block", StringComparison.OrdinalIgnoreCase));
                bool hasTimeProp = configType.GetProperties().Any(p => p.Name.Equals($"{type.Name}Time", StringComparison.OrdinalIgnoreCase));

                if (!hasBlockProp && !hasTimeProp)
                {
                    mismatches.Add($"Fork class {type.Name} introduces EIPs but has no {type.Name}Block or {type.Name}Time in GethGenesisConfigJson");
                }
            }
        }

        TestContext.Out.WriteLine($"Checked {configPropsChecked} config properties and {forkClassesChecked} fork classes with EIPs");
        mismatches.Should().BeEmpty(string.Join("\n", mismatches));
    }

    [Test]
    public void LoadParameters_maps_all_fork_eips()
    {
        List<ForkActivationInfo> forks = DiscoverGethForks();
        forks.Should().NotBeEmpty();

        // Build genesis with distinct activation values — bypass LoadStandardGethGenesis
        // to avoid hardcoded eip150Block/eip155Block/eip158Block that would shadow fork-named properties
        string configEntries = string.Join(", ", forks.Select(f => $"\"{f.GethConfigName}\": {f.ActivationValue}"));
        ChainSpec chainSpec = LoadFromString($$"""
            {
              "config": {
                "chainId": 1,
                {{configEntries}},
                "terminalTotalDifficulty": "0x0"
              },
              "difficulty": "0x1",
              "gasLimit": "0x8000000",
              "alloc": {}
            }
            """);

        List<string> mismatches = [];
        Type paramsType = chainSpec.Parameters.GetType();
        int eipCount = 0;

        foreach (ForkActivationInfo forkInfo in forks)
        {
            int forkEipCount = 0;
            foreach (string eipNumber in GetNewlyEnabledEips(forkInfo.Fork, forkInfo.Parent))
            {
                if (!EipsWithNonStandardMapping.Contains(eipNumber))
                {
                    forkEipCount++;
                    string transitionProp = $"Eip{eipNumber}{forkInfo.TransitionSuffix}";
                    PropertyInfo? paramProp = paramsType.GetProperty(transitionProp);

                    if (paramProp is not null)
                    {
                        object? actual = paramProp.GetValue(chainSpec.Parameters);
                        long? actualValue = actual is ulong u ? (long)u : (long?)actual;
                        if (actualValue != forkInfo.ActivationValue)
                        {
                            mismatches.Add($"{forkInfo.GethConfigName}: {transitionProp} expected {forkInfo.ActivationValue}, got {actualValue}");
                        }
                    }
                    else
                    {
                        mismatches.Add($"{forkInfo.GethConfigName}: IsEip{eipNumber}Enabled has no matching {transitionProp} in ChainParameters");
                    }
                }
            }

            eipCount += forkEipCount;
            TestContext.Out.WriteLine($"{forkInfo.GethConfigName}: {forkEipCount} EIPs verified");
        }

        TestContext.Out.WriteLine($"Total: {forks.Count} forks, {eipCount} EIP mappings verified");

        mismatches.Should().BeEmpty(
            "GethGenesisLoader.LoadParameters must map every EIP from Fork classes.\n" +
            "If a new EIP was added to a Fork class, update LoadParameters to set its transition timestamp.\n" +
            string.Join("\n", mismatches));
    }

    public static IEnumerable<TestCaseData> HoodiEip7949Activations
    {
        get
        {
            // Genesis
            yield return new TestCaseData(new ForkActivation(0, HoodiSpecProvider.GenesisTimestamp)) { TestName = "EIP7949_Genesis" };

            // Each transition + a "before" case for transitions with distinct timestamps
            ForkActivation[] transitions = HoodiSpecProvider.Instance.TransitionActivations;
            for (int i = 0; i < transitions.Length; i++)
            {
                ForkActivation activation = transitions[i];
                ulong? prevTimestamp = i > 0 ? transitions[i - 1].Timestamp : HoodiSpecProvider.GenesisTimestamp;

                if (activation.Timestamp > prevTimestamp)
                {
                    yield return new TestCaseData(new ForkActivation(activation.BlockNumber, activation.Timestamp!.Value - 1)) { TestName = $"EIP7949_Before_{activation.BlockNumber}" };
                }

                yield return new TestCaseData(activation) { TestName = $"EIP7949_At_{activation.BlockNumber}" };
            }

            // Future — well past the last transition
            ForkActivation last = transitions[^1];
            yield return new TestCaseData(new ForkActivation(last.BlockNumber + 1, last.Timestamp!.Value + 100_000_000)) { TestName = "EIP7949_Future" };
        }
    }

    [TestCaseSource(nameof(HoodiEip7949Activations))]
    public void Hoodi_eip7949_matches_HoodiSpecProvider(ForkActivation forkActivation)
    {
        ChainSpec chainSpec = LoadHoodiChainSpec();
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        ISpecProvider hardCodedSpec = HoodiSpecProvider.Instance;

        IReleaseSpec expectedSpec = hardCodedSpec.GetSpec(forkActivation);
        IReleaseSpec actualSpec = provider.GetSpec(forkActivation);

        PropertyInfo[] properties = typeof(IReleaseSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        List<string> differences = [];

        foreach (PropertyInfo property in properties)
        {
            // Skip collection types (reference equality would always differ)
            bool propertyTypeIsValueType = property.PropertyType.IsValueType || property.PropertyType == typeof(Address);

            // These are mainnet-specific constants baked into fork classes (e.g. Shanghai sets
            // WithdrawalTimestamp = MainnetSpecProvider.ShanghaiBlockTimestamp) that the
            // chainspec-based provider doesn't replicate — expected divergence, not a bug.
            bool skippedProperties = property.Name is nameof(IReleaseSpec.DifficultyBombDelay) or nameof(IReleaseSpec.WithdrawalTimestamp) or nameof(IReleaseSpec.Eip4844TransitionTimestamp);

            if (propertyTypeIsValueType && !skippedProperties)
            {
                object? expectedValue = property.GetValue(expectedSpec);
                object? actualValue = property.GetValue(actualSpec);
                if (!Equals(expectedValue, actualValue))
                {
                    differences.Add($"{property.Name}: expected {expectedValue}, actual {actualValue}");
                }
            }
        }

        differences.Should().BeEmpty($"at activation {forkActivation}, the following properties differ:\n{string.Join("\n", differences)}");

        provider.ChainId.Should().Be(hardCodedSpec.ChainId);
        provider.NetworkId.Should().Be(hardCodedSpec.NetworkId);
        provider.TerminalTotalDifficulty.Should().Be(hardCodedSpec.TerminalTotalDifficulty);
    }
}
