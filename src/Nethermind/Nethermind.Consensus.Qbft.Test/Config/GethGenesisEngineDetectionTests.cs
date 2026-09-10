// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Config;

/// <summary>
/// Reading a Besu consensus section out of a Geth-shaped genesis must not change how any other
/// <c>config</c> object loads: only <c>qbft</c> and <c>ibft2</c> select a BFT engine.
/// </summary>
public class GethGenesisEngineDetectionTests
{
    private static ChainSpec Load(string configBody)
    {
        string json = $$"""
            {
              "config": { "chainId": 1337, "berlinBlock": 0, {{configBody}} },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1000000",
              "difficulty": "0x1",
              "alloc": {}
            }
            """;
        return new AutoDetectingChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    [TestCase("\"clique\": { \"period\": 15, \"epoch\": 30000 }", TestName = "clique")]
    [TestCase("\"optimism\": { \"eip1559Elasticity\": 6, \"eip1559Denominator\": 50 }", TestName = "optimism")]
    [TestCase("\"ethash\": {}", TestName = "ethash")]
    [TestCase("\"blobSchedule\": { }", TestName = "blobSchedule")]
    public void OtherConfigSectionsStillLoadAsEthash(string configBody)
    {
        ChainSpec chainSpec = Load(configBody);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash));
            Assert.That(() => chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<EthashChainSpecEngineParameters>(), Throws.Nothing);
        }
    }

    [Test]
    public void ShippedGethGenesisKeepsItsEngine()
    {
        // Chains/genesis.json is a Geth file carrying config.clique; it loaded as Ethash before QBFT and must still.
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/genesis.json");
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash));
            Assert.That(() => chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<CliqueChainSpecEngineParameters>(), Throws.Exception);
        }
    }

    [TestCase("\"qbft\": { \"blockperiodseconds\": 4 }", TestName = "qbft")]
    public void BftSectionsSelectTheQbftEngine(string configBody)
    {
        ChainSpec chainSpec = Load(configBody);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Qbft));
            Assert.That(chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<QbftChainSpecEngineParameters>().BlockPeriodSeconds, Is.EqualTo(4));
        }
    }
}
