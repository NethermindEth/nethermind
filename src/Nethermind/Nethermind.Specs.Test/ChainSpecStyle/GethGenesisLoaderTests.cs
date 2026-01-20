// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
public class GethGenesisLoaderTests
{
    private static ChainSpec LoadChainSpec(string path)
    {
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);
        return chainSpec;
    }

    private static ChainSpec LoadFromString(string json)
    {
        GethGenesisLoader loader = new(new EthereumJsonSerializer());
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return loader.Load(stream);
    }

    [Test]
    public void Can_load_hoodi_eip7949()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/hoodi.json");
        ChainSpec chainSpec = LoadChainSpec(path);

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

        // Check genesis block
        chainSpec.Genesis.Should().NotBeNull();
        chainSpec.Genesis.Header.GasLimit.Should().Be(0x2255100);

        // Check allocations
        chainSpec.Allocations.Should().NotBeEmpty();
        chainSpec.Allocations[Address.Zero].Balance.Should().Be(1);

        // Check blob schedule
        chainSpec.Parameters.BlobSchedule.Should().NotBeEmpty();
        chainSpec.Parameters.BlobSchedule.Should().HaveCount(3);

        // Check deposit contract address
        chainSpec.Parameters.DepositContractAddress.Should().Be(new Address("0x00000000219ab540356cBB839Cbe05303d7705Fa"));
    }

    [Test]
    public void Can_load_minimal_geth_genesis()
    {
        const string minimalGenesis = """
        {
          "config": {
            "chainId": 12345,
            "homesteadBlock": 0,
            "eip150Block": 0,
            "eip155Block": 0,
            "eip158Block": 0
          },
          "difficulty": "0x1",
          "gasLimit": "0x8000000",
          "alloc": {
            "0x0000000000000000000000000000000000000001": { "balance": "0x1" }
          }
        }
        """;

        ChainSpec chainSpec = LoadFromString(minimalGenesis);

        chainSpec.ChainId.Should().Be(12345);
        chainSpec.NetworkId.Should().Be(12345);
        chainSpec.Genesis.Header.GasLimit.Should().Be(0x8000000);
        chainSpec.Genesis.Header.Difficulty.Should().Be(UInt256.One);
        chainSpec.Allocations.Should().HaveCount(1);
        chainSpec.Allocations[new Address("0x0000000000000000000000000000000000000001")].Balance.Should().Be(1);
    }

    [Test]
    public void Can_load_genesis_with_timestamp_forks()
    {
        const string genesis = """
        {
          "config": {
            "chainId": 1,
            "homesteadBlock": 0,
            "eip150Block": 0,
            "eip155Block": 0,
            "eip158Block": 0,
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
          },
          "difficulty": "0x1",
          "gasLimit": "0x8000000",
          "alloc": {}
        }
        """;

        ChainSpec chainSpec = LoadFromString(genesis);

        chainSpec.ChainId.Should().Be(1);
        chainSpec.ShanghaiTimestamp.Should().Be(1681338455);
        chainSpec.CancunTimestamp.Should().Be(1710338135);
        chainSpec.PragueTimestamp.Should().Be(1800000000);
        chainSpec.TerminalTotalDifficulty.Should().Be(UInt256.Zero);
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
        const string genesis = """
        {
          "config": {
            "chainId": 1,
            "homesteadBlock": 0,
            "eip150Block": 0,
            "eip155Block": 0,
            "eip158Block": 0,
            "cancunTime": 1710338135,
            "pragueTime": 1800000000,
            "blobSchedule": {
              "cancun": {
                "target": 3,
                "max": 6,
                "baseFeeUpdateFraction": 3338477
              },
              "prague": {
                "target": 6,
                "max": 9,
                "baseFeeUpdateFraction": 5007716
              }
            }
          },
          "difficulty": "0x1",
          "gasLimit": "0x8000000",
          "alloc": {}
        }
        """;

        ChainSpec chainSpec = LoadFromString(genesis);

        chainSpec.Parameters.BlobSchedule.Should().HaveCount(2);

        List<BlobScheduleSettings> blobScheduleList = [.. chainSpec.Parameters.BlobSchedule];
        // Sorted by timestamp
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
        const string genesis = """
        {
          "config": {
            "chainId": 1,
            "homesteadBlock": 0,
            "eip150Block": 0,
            "eip155Block": 0,
            "eip158Block": 0
          },
          "difficulty": "0x1",
          "gasLimit": "0x8000000",
          "alloc": {
            "0x0000000000000000000000000000000000000100": {
              "balance": "0xde0b6b3a7640000",
              "nonce": "0x1",
              "code": "0x6080604052",
              "storage": {
                "0x0000000000000000000000000000000000000000000000000000000000000001": "0x00000000000000000000000000000000000000000000000000000000000000ff"
              }
            }
          }
        }
        """;

        ChainSpec chainSpec = LoadFromString(genesis);

        Address address = new("0x0000000000000000000000000000000000000100");
        chainSpec.Allocations.Should().ContainKey(address);

        ChainSpecAllocation allocation = chainSpec.Allocations[address];
        allocation.Balance.Should().Be(UInt256.Parse("1000000000000000000")); // 1 ETH in wei
        allocation.Nonce.Should().Be(1);
        allocation.Code.Should().NotBeNull();
        allocation.Code.Should().BeEquivalentTo(new byte[] { 0x60, 0x80, 0x60, 0x40, 0x52 });
        allocation.Storage.Should().NotBeNull();
        allocation.Storage.Should().HaveCount(1);
    }

    [Test]
    public void Can_load_genesis_without_0x_prefix_in_addresses()
    {
        const string genesis = """
        {
          "config": {
            "chainId": 1,
            "homesteadBlock": 0,
            "eip150Block": 0,
            "eip155Block": 0,
            "eip158Block": 0
          },
          "difficulty": "0x1",
          "gasLimit": "0x8000000",
          "alloc": {
            "0000000000000000000000000000000000000001": { "balance": "0x1" },
            "0x0000000000000000000000000000000000000002": { "balance": "0x2" }
          }
        }
        """;

        ChainSpec chainSpec = LoadFromString(genesis);

        chainSpec.Allocations.Should().HaveCount(2);
        chainSpec.Allocations[new Address("0x0000000000000000000000000000000000000001")].Balance.Should().Be(1);
        chainSpec.Allocations[new Address("0x0000000000000000000000000000000000000002")].Balance.Should().Be(2);
    }

    [Test]
    public void AutoDetectingLoader_detects_geth_format()
    {
        const string gethGenesis = """
        {
          "config": {
            "chainId": 12345,
            "homesteadBlock": 0
          },
          "difficulty": "0x1",
          "gasLimit": "0x8000000",
          "alloc": {}
        }
        """;

        AutoDetectingChainSpecLoader loader = new(new EthereumJsonSerializer());
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(gethGenesis));
        ChainSpec chainSpec = loader.Load(stream);

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

        AutoDetectingChainSpecLoader loader = new(new EthereumJsonSerializer());
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(parityChainspec));
        ChainSpec chainSpec = loader.Load(stream);

        chainSpec.Name.Should().Be("TestNet");
        chainSpec.ChainId.Should().Be(1);
    }

    [Test]
    public void Can_load_genesis_with_deposit_contract()
    {
        const string genesis = """
        {
          "config": {
            "chainId": 1,
            "homesteadBlock": 0,
            "eip150Block": 0,
            "eip155Block": 0,
            "eip158Block": 0,
            "pragueTime": 1800000000,
            "depositContractAddress": "0x00000000219ab540356cBB839Cbe05303d7705Fa"
          },
          "difficulty": "0x1",
          "gasLimit": "0x8000000",
          "alloc": {}
        }
        """;

        ChainSpec chainSpec = LoadFromString(genesis);

        chainSpec.Parameters.DepositContractAddress.Should().Be(new Address("0x00000000219ab540356cBB839Cbe05303d7705Fa"));
        chainSpec.Parameters.Eip6110TransitionTimestamp.Should().Be(1800000000);
    }

    public static IEnumerable<TestCaseData> HoodiEip7949Activations
    {
        get
        {
            yield return new TestCaseData(new ForkActivation(0, HoodiSpecProvider.GenesisTimestamp)) { TestName = "EIP7949_Genesis" };
            yield return new TestCaseData(new ForkActivation(1, HoodiSpecProvider.ShanghaiTimestamp)) { TestName = "EIP7949_Shanghai" };
            yield return new TestCaseData(new ForkActivation(3, HoodiSpecProvider.ShanghaiTimestamp)) { TestName = "EIP7949_Post_Shanghai" };
            yield return new TestCaseData(new ForkActivation(5, HoodiSpecProvider.CancunTimestamp)) { TestName = "EIP7949_Cancun" };
            yield return new TestCaseData(new ForkActivation(7, HoodiSpecProvider.PragueTimestamp - 1)) { TestName = "EIP7949_Before_Prague" };
            yield return new TestCaseData(new ForkActivation(8, HoodiSpecProvider.PragueTimestamp)) { TestName = "EIP7949_Prague" };
            yield return new TestCaseData(new ForkActivation(9, HoodiSpecProvider.OsakaTimestamp - 1)) { TestName = "EIP7949_Before_Osaka" };
            yield return new TestCaseData(new ForkActivation(10, HoodiSpecProvider.OsakaTimestamp)) { TestName = "EIP7949_Osaka" };
            yield return new TestCaseData(new ForkActivation(11, HoodiSpecProvider.BPO1Timestamp - 1)) { TestName = "EIP7949_Before_BPO1" };
            yield return new TestCaseData(new ForkActivation(12, HoodiSpecProvider.BPO1Timestamp)) { TestName = "EIP7949_BPO1" };
            yield return new TestCaseData(new ForkActivation(13, HoodiSpecProvider.BPO2Timestamp - 1)) { TestName = "EIP7949_Before_BPO2" };
            yield return new TestCaseData(new ForkActivation(14, HoodiSpecProvider.BPO2Timestamp)) { TestName = "EIP7949_BPO2" };
            yield return new TestCaseData(new ForkActivation(15, HoodiSpecProvider.BPO2Timestamp + 100000000)) { TestName = "EIP7949_Future_BPO2" };
        }
    }

    [TestCaseSource(nameof(HoodiEip7949Activations))]
    public void Hoodi_eip7949_matches_HoodiSpecProvider(ForkActivation forkActivation)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/hoodi.json");
        ChainSpec chainSpec = LoadChainSpec(path);
        ChainSpecBasedSpecProvider provider = new(chainSpec);
        ISpecProvider hardCodedSpec = HoodiSpecProvider.Instance;

        IReleaseSpec expectedSpec = hardCodedSpec.GetSpec(forkActivation);
        IReleaseSpec actualSpec = provider.GetSpec(forkActivation);

        // Compare all boolean properties on IReleaseSpec
        PropertyInfo[] properties = typeof(IReleaseSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        List<string> differences = [];

        foreach (PropertyInfo property in properties)
        {
            if (property.PropertyType == typeof(bool))
            {
                bool expectedValue = (bool)property.GetValue(expectedSpec)!;
                bool actualValue = (bool)property.GetValue(actualSpec)!;
                if (expectedValue != actualValue)
                {
                    differences.Add($"{property.Name}: expected {expectedValue}, actual {actualValue}");
                }
            }
        }

        differences.Should().BeEmpty($"at activation {forkActivation}, the following EIPs differ:\n{string.Join("\n", differences)}");

        provider.ChainId.Should().Be(hardCodedSpec.ChainId);
        provider.NetworkId.Should().Be(hardCodedSpec.NetworkId);
        provider.TerminalTotalDifficulty.Should().Be(hardCodedSpec.TerminalTotalDifficulty);
    }
}
