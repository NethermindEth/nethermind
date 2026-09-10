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
using Nethermind.Specs.Forks;
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
        Block genesis = new BftGenesisBuilder(inner, QbftOnlyCodecSelector.Instance).Build();
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
        BftForksSchedule schedule = BftForksSchedule.Create(parameters, ulong.MaxValue);
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

    /// <summary>
    /// Besu builds its protocol schedule from cumulative spec builders, so the milestone in force at a
    /// block carries every earlier fork's rules whether the genesis names them or not, and it accepts
    /// <c>constantinoplefixblock</c> as the name for Petersburg. Alastria Red B relies on both: this is
    /// the shape of its genesis, which names no fork before Petersburg.
    /// </summary>
    [Test]
    public void OmittedForksActivateWithTheEarliestForkTheGenesisDeclares()
    {
        const string json = """
            {
              "config": {
                "chainId": 2020,
                "constantinoplefixblock": 0,
                "istanbulBlock": 100,
                "berlinBlock": 200,
                "ibft2": { "blockperiodseconds": 1, "epochlength": 30000, "requesttimeoutseconds": 10 }
              },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1000000",
              "difficulty": "0x1",
              "mixHash": "0x63746963616c2062797a616e74696e65206661756c7420746f6c6572616e6365",
              "coinbase": "0x0000000000000000000000000000000000000000",
              "alloc": {},
              "extraData": "0xf83aa00000000000000000000000000000000000000000000000000000000000000000d594000000000000000000000000000000000000000180c0"
            }
            """;
        ChainParameters parameters = Load(json).Parameters;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters.Eip1283DisableTransition, Is.EqualTo(0UL), "constantinoplefixblock names Petersburg");
            Assert.That(parameters.Eip140Transition, Is.EqualTo(0UL), "Byzantium is in force under Petersburg");
            Assert.That(parameters.Eip145Transition, Is.EqualTo(0UL), "so is Constantinople");
            Assert.That(parameters.Eip2200Transition, Is.EqualTo(100UL), "Istanbul as declared");
            Assert.That(parameters.Eip2929Transition, Is.EqualTo(200UL), "Berlin as declared");
            Assert.That(parameters.Eip1559Transition, Is.Null, "London is not declared, so nothing activates it");
        }
    }

    /// <summary>A fork the genesis skips over activates at the earliest later fork, not at block 0.</summary>
    [Test]
    public void SkippedForksDoNotFallBackToBlockZero()
    {
        const string json = """
            {
              "config": {
                "chainId": 1337,
                "istanbulBlock": 100,
                "qbft": { "blockperiodseconds": 2, "epochlength": 100 }
              },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1000000",
              "difficulty": "0x1",
              "alloc": {},
              "extraData": "0xf83aa00000000000000000000000000000000000000000000000000000000000000000d594000000000000000000000000000000000000000180c0"
            }
            """;
        ChainParameters parameters = Load(json).Parameters;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters.Eip7Transition, Is.EqualTo(100UL), "Homestead");
            Assert.That(parameters.Eip150Transition, Is.EqualTo(100UL), "Tangerine Whistle");
            Assert.That(parameters.Eip140Transition, Is.EqualTo(100UL), "Byzantium");
            Assert.That(parameters.Eip1283DisableTransition, Is.EqualTo(100UL), "Petersburg");
        }
    }

    /// <summary>
    /// Besu has no <c>tangerinewhistleblock</c> or <c>spuriousdragonblock</c> key: it reads those two
    /// forks from <c>eip150block</c> and <c>eip158block</c>, so on its path those name forks rather
    /// than single EIPs, and the forks between them and the next declared one follow from there.
    /// </summary>
    [Test]
    public void BesuReadsTangerineWhistleAndSpuriousDragonFromTheirEipKeys()
    {
        const string json = """
            {
              "config": {
                "chainId": 1337,
                "eip150Block": 5,
                "eip158Block": 7,
                "berlinBlock": 100,
                "qbft": { "blockperiodseconds": 2, "epochlength": 100 }
              },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1000000",
              "difficulty": "0x1",
              "alloc": {},
              "extraData": "0xf83aa00000000000000000000000000000000000000000000000000000000000000000d594000000000000000000000000000000000000000180c0"
            }
            """;
        ChainParameters parameters = Load(json).Parameters;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters.Eip150Transition, Is.EqualTo(5UL), "Tangerine Whistle");
            Assert.That(parameters.Eip161abcTransition, Is.EqualTo(7UL), "Spurious Dragon");
            Assert.That(parameters.Eip7Transition, Is.EqualTo(5UL), "Homestead is in force under Tangerine Whistle");
            Assert.That(parameters.Eip140Transition, Is.EqualTo(100UL), "Byzantium follows the next declared fork");
            Assert.That(parameters.Eip2929Transition, Is.EqualTo(100UL), "Berlin as declared");
        }
    }

    /// <summary>Besu refuses a genesis that names Petersburg twice with different blocks.</summary>
    [Test]
    public void ConflictingPetersburgSpellingsAreRejected()
    {
        const string json = """
            {
              "config": {
                "chainId": 1337,
                "petersburgBlock": 1,
                "constantinoplefixblock": 2,
                "qbft": { "blockperiodseconds": 2, "epochlength": 100 }
              },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1000000",
              "difficulty": "0x1",
              "alloc": {}
            }
            """;
        Assert.That(() => Load(json), Throws.InstanceOf<InvalidDataException>().With.Message.Contains(nameof(ConstantinopleFix)));
    }

    /// <summary>
    /// Geth evaluates each fork on its own, so the cumulative fill above must not reach a plain Geth
    /// genesis: one that skips Byzantium keeps it switched off.
    /// </summary>
    [Test]
    public void PlainGethGenesisKeepsPerForkActivation()
    {
        const string json = """
            {
              "config": { "chainId": 5, "ethash": {}, "istanbulBlock": 100 },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1000000",
              "difficulty": "0x1",
              "alloc": {}
            }
            """;
        ChainParameters parameters = Load(json).Parameters;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters.Eip2200Transition, Is.EqualTo(100UL), "Istanbul as declared");
            Assert.That(parameters.Eip140Transition, Is.Null, "Byzantium is not declared and Geth does not imply it");
            Assert.That(parameters.Eip145Transition, Is.Null, "nor Constantinople");
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
