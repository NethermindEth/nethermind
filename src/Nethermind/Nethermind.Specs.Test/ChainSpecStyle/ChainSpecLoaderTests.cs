// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
public class ChainSpecLoaderTests
{
    private static ChainSpec LoadChainSpec(string path)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance);
        var chainSpec = loader.LoadEmbeddedOrFromFile(path);
        return chainSpec;
    }

    [Test]
    public void Can_load_mainnet()
    {
        new EthashChainSpecEngineParameters();
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/foundation.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"fork base fee");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(1), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Ethereum"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("ethereum"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        chainSpec.HomesteadBlockNumber.Should().Be(MainnetSpecProvider.HomesteadBlockNumber);
        chainSpec.DaoForkBlockNumber.Should().Be(1920000);
        chainSpec.TangerineWhistleBlockNumber.Should().Be(MainnetSpecProvider.TangerineWhistleBlockNumber);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(MainnetSpecProvider.SpuriousDragonBlockNumber);
        chainSpec.ByzantiumBlockNumber.Should().Be(MainnetSpecProvider.ByzantiumBlockNumber);
        chainSpec.ConstantinopleBlockNumber.Should().Be(null);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(MainnetSpecProvider.ConstantinopleFixBlockNumber);
        chainSpec.IstanbulBlockNumber.Should().Be(MainnetSpecProvider.IstanbulBlockNumber);
        chainSpec.MuirGlacierNumber.Should().Be(MainnetSpecProvider.MuirGlacierBlockNumber);
        chainSpec.BerlinBlockNumber.Should().Be(MainnetSpecProvider.BerlinBlockNumber);
        chainSpec.LondonBlockNumber.Should().Be(MainnetSpecProvider.LondonBlockNumber);
        chainSpec.ArrowGlacierBlockNumber.Should().Be(MainnetSpecProvider.ArrowGlacierBlockNumber);
        chainSpec.GrayGlacierBlockNumber.Should().Be(MainnetSpecProvider.GrayGlacierBlockNumber);
        chainSpec.ShanghaiTimestamp.Should().Be(MainnetSpecProvider.ShanghaiBlockTimestamp);
        chainSpec.ShanghaiTimestamp.Should().Be(MainnetSpecProvider.Instance.TimestampFork);
    }

    [Test]
    public void Can_load_spaceneth()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/spaceneth.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.NetworkId, Is.EqualTo(99), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Spaceneth"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("spaceneth"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.NethDev), "engine");

        chainSpec.HomesteadBlockNumber.Should().Be(0L);
        chainSpec.DaoForkBlockNumber.Should().Be(null);
        chainSpec.TangerineWhistleBlockNumber.Should().Be(0L);
        chainSpec.SpuriousDragonBlockNumber.Should().Be(0L);
        chainSpec.ByzantiumBlockNumber.Should().Be(0L);
        chainSpec.ConstantinopleBlockNumber.Should().Be(0L);
        chainSpec.ConstantinopleFixBlockNumber.Should().Be(0L);
        chainSpec.IstanbulBlockNumber.Should().Be(0L);
        chainSpec.MuirGlacierNumber.Should().Be(null);
        chainSpec.BerlinBlockNumber.Should().Be(0L);
        chainSpec.LondonBlockNumber.Should().Be(0L);
        chainSpec.ArrowGlacierBlockNumber.Should().Be(null);
        chainSpec.GrayGlacierBlockNumber.Should().Be(null);
    }

    [Test]
    public void Can_load_sepolia()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/sepolia.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.NetworkId, Is.EqualTo(11155111), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Sepolia Testnet"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("sepolia"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        chainSpec.LondonBlockNumber.Should().Be(0L);
        chainSpec.ShanghaiTimestamp.Should().Be(1677557088);
    }

    [Test]
    public void Can_load_hoodi()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/hoodi.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.NetworkId, Is.EqualTo(560048), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("Hoodi Testnet"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.DataDir, Is.EqualTo("hoodi"), $"{nameof(chainSpec.DataDir)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

        chainSpec.DaoForkBlockNumber.Should().Be(null);
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
        chainSpec.PragueTimestamp.Should().Be(HoodiSpecProvider.PragueTimestamp);
    }

    [Test]
    public void Can_load_posdao_with_openethereum_pricing_transitions()
    {
        // TODO: modexp 2565
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/posdao.json");
        ChainSpec chainSpec = LoadChainSpec(path);
        chainSpec.Parameters.Eip152Transition.Should().Be(15);
        chainSpec.Parameters.Eip1108Transition.Should().Be(10);
    }

    [Test]
    public void All_ChainSpecParamsJson_properties_should_have_matching_ChainParameters_properties()
    {
        // Properties mapped to ChainSpec directly, not ChainParameters
        HashSet<string> mappedToChainSpec = ["ChainId", "NetworkId"];
        Dictionary<string, string> nameMapping = new() { ["EnsRegistrar"] = "Registrar" };

        HashSet<string> domainPropertyNames = [];
        foreach (PropertyInfo prop in typeof(ChainParameters).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            domainPropertyNames.Add(prop.Name);

        List<string> missing = [];
        foreach (PropertyInfo prop in typeof(ChainSpecParamsJson).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (mappedToChainSpec.Contains(prop.Name)) continue;
            string expectedName = nameMapping.TryGetValue(prop.Name, out string? mapped) ? mapped : prop.Name;
            if (!domainPropertyNames.Contains(expectedName))
                missing.Add(expectedName);
        }

        missing.Sort();
        missing.Should().BeEmpty(
            "every ChainSpecParamsJson property should have a corresponding ChainParameters property. " +
            "If you added a new field to ChainSpecParamsJson, add it to ChainParameters too.");
    }

    [Test]
    public void All_ChainParameters_properties_should_have_matching_ChainSpecParamsJson_properties()
    {
        Dictionary<string, string> nameMapping = new() { ["Registrar"] = "EnsRegistrar" };

        HashSet<string> jsonPropertyNames = [];
        foreach (PropertyInfo prop in typeof(ChainSpecParamsJson).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            jsonPropertyNames.Add(prop.Name);

        List<string> missing = [];
        foreach (PropertyInfo prop in typeof(ChainParameters).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string expectedName = nameMapping.TryGetValue(prop.Name, out string? mapped) ? mapped : prop.Name;
            if (!jsonPropertyNames.Contains(expectedName))
                missing.Add(prop.Name);
        }

        missing.Sort();
        missing.Should().BeEmpty(
            "every ChainParameters property should have a corresponding ChainSpecParamsJson property. " +
            "If you added a new field to ChainParameters, add it to ChainSpecParamsJson too.");
    }

    [Test]
    public void All_ChainSpecParamsJson_properties_should_be_mapped_in_loader()
    {
        // Properties excluded due to ChainSpecLoader.ValidateParams constraints:
        // - Eip1706Transition: throws when set together with Eip2200Transition
        // - Eip1283ReenableTransition: must equal Eip1706Transition when Eip1283DisableTransition is set
        HashSet<string> excludedProperties = ["Eip1706Transition", "Eip1283ReenableTransition"];

        // Set all ChainSpecParamsJson properties to non-default test values
        ChainSpecParamsJson paramsJson = new();
        foreach (PropertyInfo prop in typeof(ChainSpecParamsJson).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (excludedProperties.Contains(prop.Name)) continue;
            object? testValue = CreateTestValue(prop.PropertyType);
            if (testValue is not null)
                prop.SetValue(paramsJson, testValue);
        }

        // Serialize and wrap in a minimal valid chain spec JSON
        EthereumJsonSerializer serializer = new();
        string paramsStr = serializer.Serialize(paramsJson);
        string json = $$"""
            {
                "name": "Test",
                "engine": { "NethDev": {} },
                "params": {{paramsStr}},
                "genesis": {
                    "seal": { "ethereum": { "nonce": "0x0", "mixHash": "0x0000000000000000000000000000000000000000000000000000000000000000" } },
                    "difficulty": "0x1",
                    "gasLimit": "0x1000000",
                    "timestamp": "0x0"
                }
            }
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        ChainSpecLoader loader = new(serializer, LimboLogs.Instance);
        ChainSpec chainSpec = loader.Load(stream);

        // Compare against a baseline to detect unmapped properties,
        // including those with non-zero field initializers (e.g. Eip2935RingBufferSize)
        ChainParameters baseline = new();
        foreach (PropertyInfo prop in typeof(ChainParameters).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (excludedProperties.Contains(prop.Name)) continue;
            object? loadedValue = prop.GetValue(chainSpec.Parameters);
            object? baselineValue = prop.GetValue(baseline);
            ValuesMatch(loadedValue, baselineValue).Should().BeFalse(
                $"ChainParameters.{prop.Name} still has its default value ({baselineValue}) after loading. " +
                "Ensure it is mapped in ChainSpecLoader.LoadParameters.");
        }
    }

    private static object? CreateTestValue(Type type)
    {
        Type underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        if (underlyingType == typeof(long)) return 12345L;
        if (underlyingType == typeof(ulong)) return 98765UL;
        if (underlyingType == typeof(int)) return 54321;
        if (underlyingType == typeof(UInt256)) return (UInt256)77777;
        if (underlyingType == typeof(Address)) return new Address("0x1111111111111111111111111111111111111111");
        if (underlyingType == typeof(Hash256)) return new Hash256("0x1111111111111111111111111111111111111111111111111111111111111111");
        if (type == typeof(SortedSet<BlobScheduleSettings>))
            return new SortedSet<BlobScheduleSettings> { new() { Timestamp = 100, Target = 3, Max = 6, BaseFeeUpdateFraction = 3338477 } };
        return null;
    }

    private static bool ValuesMatch(object? loaded, object? baseline)
    {
        if (loaded is null && baseline is null) return true;
        if (loaded is null || baseline is null) return false;
        if (loaded is System.Collections.ICollection cl && baseline is System.Collections.ICollection cb)
            return cl.Count == cb.Count;
        return loaded.Equals(baseline);
    }
}
