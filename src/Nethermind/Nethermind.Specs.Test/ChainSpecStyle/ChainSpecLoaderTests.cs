// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
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
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);
        return chainSpec;
    }

    [Test]
    public void Can_load_mainnet()
    {
        new EthashChainSpecEngineParameters();
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/foundation.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei), $"fork base fee");
            Assert.That(chainSpec.NetworkId, Is.EqualTo(1), $"{nameof(chainSpec.NetworkId)}");
            Assert.That(chainSpec.Name, Is.EqualTo("Ethereum"), $"{nameof(chainSpec.Name)}");
            Assert.That(chainSpec.DataDir, Is.EqualTo("ethereum"), $"{nameof(chainSpec.Name)}");
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

            Assert.That(chainSpec.HomesteadBlockNumber, Is.EqualTo(MainnetSpecProvider.HomesteadBlockNumber));
            Assert.That(chainSpec.DaoForkBlockNumber, Is.EqualTo(1920000));
            Assert.That(chainSpec.TangerineWhistleBlockNumber, Is.EqualTo(MainnetSpecProvider.TangerineWhistleBlockNumber));
            Assert.That(chainSpec.SpuriousDragonBlockNumber, Is.EqualTo(MainnetSpecProvider.SpuriousDragonBlockNumber));
            Assert.That(chainSpec.ByzantiumBlockNumber, Is.EqualTo(MainnetSpecProvider.ByzantiumBlockNumber));
            Assert.That(chainSpec.ConstantinopleBlockNumber, Is.EqualTo(null));
            Assert.That(chainSpec.ConstantinopleFixBlockNumber, Is.EqualTo(MainnetSpecProvider.ConstantinopleFixBlockNumber));
            Assert.That(chainSpec.IstanbulBlockNumber, Is.EqualTo(MainnetSpecProvider.IstanbulBlockNumber));
            Assert.That(chainSpec.MuirGlacierNumber, Is.EqualTo(MainnetSpecProvider.MuirGlacierBlockNumber));
            Assert.That(chainSpec.BerlinBlockNumber, Is.EqualTo(MainnetSpecProvider.BerlinBlockNumber));
            Assert.That(chainSpec.LondonBlockNumber, Is.EqualTo(MainnetSpecProvider.LondonBlockNumber));
            Assert.That(chainSpec.ArrowGlacierBlockNumber, Is.EqualTo(MainnetSpecProvider.ArrowGlacierBlockNumber));
            Assert.That(chainSpec.GrayGlacierBlockNumber, Is.EqualTo(MainnetSpecProvider.GrayGlacierBlockNumber));
            Assert.That(chainSpec.ShanghaiTimestamp, Is.EqualTo(MainnetSpecProvider.ShanghaiBlockTimestamp));
            Assert.That(chainSpec.ShanghaiTimestamp, Is.EqualTo(MainnetSpecProvider.Instance.TimestampFork));
        }
    }

    [Test]
    public void Can_load_spaceneth()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/spaceneth.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.NetworkId, Is.EqualTo(99), $"{nameof(chainSpec.NetworkId)}");
            Assert.That(chainSpec.Name, Is.EqualTo("Spaceneth"), $"{nameof(chainSpec.Name)}");
            Assert.That(chainSpec.DataDir, Is.EqualTo("spaceneth"), $"{nameof(chainSpec.Name)}");
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.NethDev), "engine");

            Assert.That(chainSpec.HomesteadBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.DaoForkBlockNumber, Is.EqualTo(null));
            Assert.That(chainSpec.TangerineWhistleBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.SpuriousDragonBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.ByzantiumBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.ConstantinopleBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.ConstantinopleFixBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.IstanbulBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.MuirGlacierNumber, Is.EqualTo(null));
            Assert.That(chainSpec.BerlinBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.LondonBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.ArrowGlacierBlockNumber, Is.EqualTo(null));
            Assert.That(chainSpec.GrayGlacierBlockNumber, Is.EqualTo(null));
        }
    }

    [Test]
    public void Can_load_sepolia()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/sepolia.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.NetworkId, Is.EqualTo(11155111), $"{nameof(chainSpec.NetworkId)}");
            Assert.That(chainSpec.Name, Is.EqualTo("Sepolia Testnet"), $"{nameof(chainSpec.Name)}");
            Assert.That(chainSpec.DataDir, Is.EqualTo("sepolia"), $"{nameof(chainSpec.Name)}");
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

            Assert.That(chainSpec.LondonBlockNumber, Is.EqualTo(0L));
            Assert.That(chainSpec.ShanghaiTimestamp, Is.EqualTo(1677557088));
        }
    }

    [Test]
    public void Can_load_hoodi()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/hoodi.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.NetworkId, Is.EqualTo(560048), $"{nameof(chainSpec.NetworkId)}");
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash), "engine");

            Assert.That(chainSpec.DaoForkBlockNumber, Is.EqualTo(null));
            Assert.That(chainSpec.TangerineWhistleBlockNumber, Is.EqualTo(0));
            Assert.That(chainSpec.SpuriousDragonBlockNumber, Is.EqualTo(0));
            Assert.That(chainSpec.ByzantiumBlockNumber, Is.EqualTo(0));
            Assert.That(chainSpec.ConstantinopleBlockNumber, Is.EqualTo(0));
            Assert.That(chainSpec.ConstantinopleFixBlockNumber, Is.EqualTo(0));
            Assert.That(chainSpec.IstanbulBlockNumber, Is.EqualTo(0));
            Assert.That(chainSpec.BerlinBlockNumber, Is.EqualTo(0));
            Assert.That(chainSpec.LondonBlockNumber, Is.EqualTo(0));
            Assert.That(chainSpec.ShanghaiTimestamp, Is.EqualTo(0));
            Assert.That(chainSpec.CancunTimestamp, Is.EqualTo(0));
            Assert.That(chainSpec.PragueTimestamp, Is.EqualTo(HoodiSpecProvider.PragueTimestamp));
        }
    }

    [Test]
    public void Can_load_posdao_with_openethereum_pricing_transitions()
    {
        // TODO: modexp 2565
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/posdao.json");
        ChainSpec chainSpec = LoadChainSpec(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.Parameters.Eip152Transition, Is.EqualTo(15));
            Assert.That(chainSpec.Parameters.Eip1108Transition, Is.EqualTo(10));
        }
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
            Assert.That(ValuesMatch(loadedValue, baselineValue), Is.False, $"ChainParameters.{prop.Name} still has its default value ({baselineValue}) after loading. " +
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
