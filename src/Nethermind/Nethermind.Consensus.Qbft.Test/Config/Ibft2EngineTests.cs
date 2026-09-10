// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Text;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Blocks;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Config;

/// <summary>
/// The IBFT 2.0 engine: a Besu <c>ibft2</c> genesis selects it, its headers hash with the IBFT 2.0
/// codec, and a chain migrated to QBFT stays a QBFT chain.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class Ibft2EngineTests
{
    private const string Ibft2Extra = "0xf83aa00000000000000000000000000000000000000000000000000000000000000000d594000000000000000000000000000000000000000180c0";

    private static ChainSpec Load(string configBody, string extraData = Ibft2Extra)
    {
        string json = $$"""
            {
              "config": { "chainId": 4242, "berlinBlock": 0, {{configBody}} },
              "nonce": "0x0",
              "timestamp": "0x0",
              "gasLimit": "0x1000000",
              "difficulty": "0x1",
              "mixHash": "0x63746963616c2062797a616e74696e65206661756c7420746f6c6572616e6365",
              "coinbase": "0x0000000000000000000000000000000000000000",
              "alloc": {},
              "extraData": "{{extraData}}"
            }
            """;
        return new AutoDetectingChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    [Test]
    public void Ibft2GenesisSelectsTheIbft2Engine()
    {
        ChainSpec chainSpec = Load("\"ibft2\": { \"blockperiodseconds\": 5, \"epochlength\": 20000, \"requesttimeoutseconds\": 10, \"blockreward\": \"0x3\" }");
        Ibft2ChainSpecEngineParameters parameters = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<Ibft2ChainSpecEngineParameters>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Ibft2));
            Assert.That(chainSpec.ChainId, Is.EqualTo(4242UL));
            Assert.That(parameters.BlockPeriodSeconds, Is.EqualTo(5));
            Assert.That(parameters.EpochLength, Is.EqualTo(20_000));
            Assert.That(parameters.RequestTimeoutSeconds, Is.EqualTo(10));
            Assert.That(parameters.BlockReward, Is.EqualTo((Int256.UInt256)3));
            Assert.That(chainSpec.Parameters.MaximumExtraDataSize, Is.EqualTo(int.MaxValue));
        }
    }

    [Test]
    public void Ibft2TransitionsAreFoldedIntoTheSchedule()
    {
        ChainSpec chainSpec = Load("""
            "ibft2": { "blockperiodseconds": 2, "epochlength": 100 },
            "transitions": { "ibft2": [ { "block": 50, "blockperiodseconds": 8, "blockreward": "0x7" } ] }
            """);
        Ibft2ChainSpecEngineParameters parameters = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<Ibft2ChainSpecEngineParameters>();
        BftForksSchedule schedule = BftForksSchedule.Create(parameters, ulong.MaxValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters.Transitions, Has.Count.EqualTo(1));
            Assert.That(schedule.GetFork(49, 0).BlockPeriodSeconds, Is.EqualTo(2));
            Assert.That(schedule.GetFork(50, 0).BlockPeriodSeconds, Is.EqualTo(8));
            Assert.That(schedule.GetFork(50, 0).BlockReward, Is.EqualTo((Int256.UInt256)7));
            Assert.That(schedule.GetFork(50, 0).IsValidatorContractMode, Is.False, "IBFT 2.0 has no validator contract mode");
        }
    }

    [Test]
    public void MigratedGenesisCarryingBothSectionsIsAQbftChain()
    {
        // Besu keeps the ibft2 section of a migrated chain so the era below startblock keeps its own settings.
        ChainSpec chainSpec = Load("""
            "ibft2": { "blockperiodseconds": 2, "epochlength": 100, "requesttimeoutseconds": 4 },
            "qbft": { "blockperiodseconds": 5, "epochlength": 30000, "requesttimeoutseconds": 8, "startblock": 300 }
            """);
        QbftChainSpecEngineParameters parameters = chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<QbftChainSpecEngineParameters>();
        BftForksSchedule schedule = BftForksSchedule.Create(parameters, ulong.MaxValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.SealEngineType, Is.EqualTo(SealEngineType.Qbft), "the chain migrated to QBFT");
            Assert.That(parameters.StartBlock, Is.EqualTo(300UL));
            Assert.That(parameters.Ibft2!.EpochLength, Is.EqualTo(100));
            Assert.That(schedule.GetFork(0, 0).BlockPeriodSeconds, Is.EqualTo(2), "IBFT 2.0 era");
            Assert.That(schedule.GetFork(300, 0).BlockPeriodSeconds, Is.EqualTo(5), "QBFT era");
            Assert.That(() => chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<Ibft2ChainSpecEngineParameters>(), Throws.Exception,
                "only one engine is selected, so the ibft2 section is not offered as its own engine");
        }
    }

    [Test]
    public void Ibft2HeadersHashWithTheIbft2Codec()
    {
        Address[] validators = [QbftTestData.Addr(1), QbftTestData.Addr(2), QbftTestData.Addr(3)];
        BftExtraData extraData = new(QbftTestData.ZeroVanity(), [], null, 3, validators);
        BlockHeader ibft2 = QbftTestData.BftHeader(7, validators[0], Ibft2ExtraDataCodec.Instance.Encode(extraData)).TestObject;
        BlockHeader qbft = QbftTestData.BftHeader(7, validators[0], QbftExtraDataCodec.Instance.Encode(extraData)).TestObject;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BftBlockHashing.CalculateOnChainHash(ibft2, Ibft2ExtraDataCodec.Instance),
                Is.Not.EqualTo(BftBlockHashing.CalculateOnChainHash(qbft, QbftExtraDataCodec.Instance)),
                "the two codecs encode the same extra data differently");
            Assert.That(QbftTestData.ToQbftHeader(ibft2, Ibft2ExtraDataCodec.Instance).Hash!.ValueHash256,
                Is.EqualTo(BftBlockHashing.CalculateOnChainHash(ibft2, Ibft2ExtraDataCodec.Instance)));
        }
    }

    [Test]
    public void SealValidationAcceptsAnIbft2SealedHeader()
    {
        List<PrivateKey> keys = QbftTestData.Keys(4);
        Address[] validators = QbftTestMessages.Addresses(keys);
        System.Array.Sort(validators);

        BftExtraData genesisExtra = new(QbftTestData.ZeroVanity(), [], null, 0, validators);
        BftExtraData unsealedExtra = new(QbftTestData.ZeroVanity(), [], null, 2, validators);

        // The commit digest ignores the seals, so it can be taken from an identical unsealed header.
        TestChain probe = new();
        probe.Add(validators[0], genesisExtra, codec: Ibft2ExtraDataCodec.Instance);
        BlockHeader unsealed = probe.Add(validators[1], unsealedExtra, codec: Ibft2ExtraDataCodec.Instance);
        ValueHash256 digest = BftBlockHashing.CalculateCommitSealDigest(unsealed, Ibft2ExtraDataCodec.Instance);

        // Three of four validators is the quorum.
        EthereumEcdsa ecdsa = new(1);
        Signature[] seals = [ecdsa.Sign(keys[0], in digest), ecdsa.Sign(keys[1], in digest), ecdsa.Sign(keys[2], in digest)];

        TestChain chain = new();
        chain.Add(validators[0], genesisExtra, codec: Ibft2ExtraDataCodec.Instance);
        BlockHeader parent = chain.Head;
        BftBlockHeader sealedHeader = chain.Add(validators[1], unsealedExtra.WithSeals(seals, 2), codec: Ibft2ExtraDataCodec.Instance);

        BftBlockInterface blockInterface = new(Ibft2OnlyCodecSelector.Instance);
        IValidatorProvider validatorProvider = BlockValidatorProvider.NonForking(chain.BlockTree, new EpochManager(30_000), blockInterface);
        BftSealValidator sealValidator = new(
            validatorProvider,
            blockInterface,
            BftForksSchedule.Create(new Ibft2ChainSpecEngineParameters(), ulong.MaxValue),
            LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sealValidator.ValidateParams(parent, sealedHeader, out string? error), Is.True, error);
            Assert.That(sealValidator.ValidateSeal(sealedHeader, force: true), Is.True);
        }
    }
}
