// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Config;

/// <summary>A Besu <c>genesis.json</c> (Geth layout plus <c>qbft</c>, <c>transitions</c>, <c>contractSizeLimit</c> and <c>discovery</c>) loads directly as a QBFT chainspec.</summary>
public class BesuGenesisLoaderTests
{
    private static readonly Hash256 RbbGenesisHash = new("0xa7ce1b4328b704f45901db430f4b52eb1bc633cf78b8bde63c60e0ee84c431ed");

    private static ChainSpec Load(Stream stream) => new AutoDetectingChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(stream);

    private static ChainSpec Load(string json) => Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static ChainSpec LoadRbbBesuGenesis()
    {
        using FileStream stream = File.OpenRead(Path.Combine(TestContext.CurrentContext.TestDirectory, "Genesis", "rbb-besu-genesis.json"));
        return Load(stream);
    }

    [Test]
    public void LoadsQbftEngineParametersFromBesuGenesis()
    {
        ChainSpec chainSpec = LoadRbbBesuGenesis();
        QbftChainSpecEngineParameters parameters = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<QbftChainSpecEngineParameters>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Qbft));
            Assert.That(chainSpec.ChainId, Is.EqualTo(12120014UL));
            Assert.That(parameters.BlockPeriodSeconds, Is.EqualTo(4));
            Assert.That(parameters.EpochLength, Is.EqualTo(30_000));
            Assert.That(parameters.RequestTimeoutSeconds, Is.EqualTo(8));
            Assert.That(chainSpec.Parameters.MaxCodeSize, Is.EqualTo(2147483647L), "contractSizeLimit");
            Assert.That(chainSpec.Parameters.MaximumExtraDataSize, Is.EqualTo(int.MaxValue));
            Assert.That(chainSpec.Bootnodes, Has.Length.EqualTo(7), "discovery.bootnodes");
            Assert.That(chainSpec.BerlinBlockNumber, Is.EqualTo(0UL));
        }
    }

    [Test]
    public void BesuGenesisProducesTheSameGenesisHashAsTheChainspec()
    {
        ChainSpec chainSpec = LoadRbbBesuGenesis();
        ChainSpecBasedSpecProvider specProvider = new(chainSpec);
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        GenesisBuilder inner = new(chainSpec, specProvider, worldState, Substitute.For<ITransactionProcessor>());
        Block genesis = new QbftGenesisBuilder(inner, QbftOnlyCodecSelector.Instance).Build();
        Assert.That(genesis.Hash, Is.EqualTo(RbbGenesisHash));
    }

    [Test]
    public void TransitionsAreFoldedIntoTheEngineParameters()
    {
        const string json = """
            {
              "config": {
                "chainId": 1337,
                "berlinBlock": 0,
                "qbft": { "blockperiodseconds": 2, "epochlength": 100, "requesttimeoutseconds": 4, "validatorcontractaddress": "0x0000000000000000000000000000000000008888" },
                "transitions": {
                  "qbft": [
                    { "block": 10, "blockperiodseconds": 5, "emptyblockperiodseconds": 60 },
                    { "block": 20, "validatorselectionmode": "blockheader", "validators": ["0x0000000000000000000000000000000000000001"], "miningbeneficiary": "0x0000000000000000000000000000000000000009", "blockreward": "0x5" }
                  ]
                }
              },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1fffffffffffff",
              "difficulty": "0x1",
              "mixHash": "0x63746963616c2062797a616e74696e65206661756c7420746f6c6572616e6365",
              "coinbase": "0x0000000000000000000000000000000000000000",
              "alloc": {},
              "extraData": "0xf83aa00000000000000000000000000000000000000000000000000000000000000000d594000000000000000000000000000000000000000180c0"
            }
            """;
        ChainSpec chainSpec = Load(json);
        QbftChainSpecEngineParameters parameters = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<QbftChainSpecEngineParameters>();
        QbftForksSchedule schedule = QbftForksSchedule.Create(parameters, ulong.MaxValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Qbft));
            Assert.That(parameters.IsValidatorContractMode, Is.True);
            Assert.That(parameters.Transitions, Has.Count.EqualTo(2));
            Assert.That(schedule.GetFork(5, 0).BlockPeriodSeconds, Is.EqualTo(2));
            Assert.That(schedule.GetFork(10, 0).BlockPeriodSeconds, Is.EqualTo(5));
            Assert.That(schedule.GetFork(10, 0).EmptyBlockPeriodSeconds, Is.EqualTo(60));
            Assert.That(schedule.GetFork(10, 0).IsValidatorContractMode, Is.True);
            Assert.That(schedule.GetFork(20, 0).IsValidatorContractMode, Is.False);
            Assert.That(schedule.GetFork(20, 0).MiningBeneficiary, Is.EqualTo(QbftTestData.Addr(9)));
            Assert.That(schedule.GetFork(20, 0).BlockReward, Is.EqualTo((Nethermind.Int256.UInt256)5));
            Assert.That(schedule.GetValidatorOverride(20), Is.EqualTo(new[] { QbftTestData.Addr(1) }));
            Assert.That(chainSpec.Parameters.MaxCodeSize, Is.EqualTo(0x6000L), "default EIP-170 limit without contractSizeLimit");
        }
    }

    [Test]
    public void PlainGethGenesisStillLoadsAsEthash()
    {
        const string json = """
            {
              "config": { "chainId": 5, "ethash": {}, "berlinBlock": 0 },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1000000",
              "difficulty": "0x1",
              "alloc": {}
            }
            """;
        ChainSpec chainSpec = Load(json);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ethash));
            Assert.That(chainSpec.Bootnodes, Is.Empty);
        }
    }
}
