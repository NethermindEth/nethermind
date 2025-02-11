// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.AuRa.Test;

public class ChainSpecLoaderTest
{
    private static ChainSpec LoadChainSpec(string path)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        var chainSpec = loader.LoadEmbeddedOrFromFile(path);
        return chainSpec;
    }

    [Test]
    public void Can_load_gnosis()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/gnosis.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"fork base fee");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(100), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("GnosisChain"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.AuRa), "engine");

        int berlinGnosisBlockNumber = 16101500;
        chainSpec.Parameters.Eip2565Transition.Should().Be(berlinGnosisBlockNumber);
        chainSpec.Parameters.Eip2929Transition.Should().Be(berlinGnosisBlockNumber);
        chainSpec.Parameters.Eip2930Transition.Should().Be(berlinGnosisBlockNumber);

        chainSpec.Parameters.TerminalTotalDifficulty.ToString()
            .Should().Be("8626000000000000000000058750000000000000000000");

        var auraParams = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();

        auraParams.WithdrawalContractAddress.ToString(true)
            .Should().Be("0x0B98057eA310F4d31F2a452B414647007d1645d9");
    }

    [Test]
    public void Can_load_chiado()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/chiado.json");
        ChainSpec chainSpec = LoadChainSpec(path);

        Assert.That(chainSpec.Parameters.Eip1559BaseFeeInitialValue, Is.EqualTo(1.GWei()), $"fork base fee");
        Assert.That(chainSpec.NetworkId, Is.EqualTo(10200), $"{nameof(chainSpec.NetworkId)}");
        Assert.That(chainSpec.Name, Is.EqualTo("chiado"), $"{nameof(chainSpec.Name)}");
        Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.AuRa), "engine");

        chainSpec.Parameters.TerminalTotalDifficulty.ToString()
            .Should().Be("231707791542740786049188744689299064356246512");

        var auraParams = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();

        auraParams.WithdrawalContractAddress.ToString(true)
            .Should().Be("0xb97036A26259B7147018913bD58a774cf91acf25");

        chainSpec.ShanghaiTimestamp.Should().Be(ChiadoSpecProvider.ShanghaiTimestamp);
        chainSpec.ShanghaiTimestamp.Should().Be(ChiadoSpecProvider.Instance.TimestampFork);
    }

    [Test]
    public void Can_load_posdao_with_rewriteBytecode()
    {
        // TODO: modexp 2565
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/posdao.json");
        ChainSpec chainSpec = LoadChainSpec(path);
        IDictionary<long, IDictionary<Address, byte[]>> expected = new Dictionary<long, IDictionary<Address, byte[]>>
        {
            {
                21300000, new Dictionary<Address, byte[]>()
                {
                    {new Address("0x1234000000000000000000000000000000000001"), Bytes.FromHexString("0x111")},
                    {new Address("0x1234000000000000000000000000000000000002"), Bytes.FromHexString("0x222")},
                }
            }
        };

        var auraParams = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();

        auraParams.RewriteBytecode.Should().BeEquivalentTo(expected);
    }
}
