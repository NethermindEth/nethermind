// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Bft;

[Parallelizable(ParallelScope.All)]
public class BftBlockHashingTests
{
    private static readonly QbftExtraDataCodec Codec = QbftExtraDataCodec.Instance;
    private static readonly Address[] Validators = [QbftTestData.Addr(1), QbftTestData.Addr(2), QbftTestData.Addr(3)];

    private static BlockHeader Header(int round, IReadOnlyList<Signature> seals) =>
        QbftTestData.BftHeader(5, Validators[0], Codec.Encode(new BftExtraData(QbftTestData.ZeroVanity(), seals, null, round, Validators))).TestObject;

    [Test]
    public void OnChainHashIgnoresRoundAndSeals()
    {
        ValueHash256 unsealed = BftBlockHashing.CalculateOnChainHash(Header(0, []), Codec);
        ValueHash256 sealedInRound3 = BftBlockHashing.CalculateOnChainHash(Header(3, [QbftTestData.Seal(1, 10, 0)]), Codec);
        Assert.That(sealedInRound3, Is.EqualTo(unsealed));
    }

    [Test]
    public void CommitSealDigestDependsOnRoundButNotOnSeals()
    {
        ValueHash256 round0 = BftBlockHashing.CalculateCommitSealDigest(Header(0, []), Codec);
        ValueHash256 round0Sealed = BftBlockHashing.CalculateCommitSealDigest(Header(0, [QbftTestData.Seal(1, 10, 0)]), Codec);
        ValueHash256 round1 = BftBlockHashing.CalculateCommitSealDigest(Header(1, []), Codec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(round0Sealed, Is.EqualTo(round0));
            Assert.That(round1, Is.Not.EqualTo(round0));
        }
    }

    [Test]
    public void UnsealedRoundZeroDigestEqualsOnChainHash()
    {
        // With round 0 and no seals the digest encoding and the on-chain encoding are identical.
        BlockHeader header = Header(0, []);
        Assert.That(BftBlockHashing.CalculateCommitSealDigest(header, Codec), Is.EqualTo(BftBlockHashing.CalculateOnChainHash(header, Codec)));
    }

    [Test]
    public void GenesisHashesAsIs()
    {
        BlockHeader genesis = QbftTestData.BftHeader(0, Address.Zero, Codec.Encode(new BftExtraData(QbftTestData.ZeroVanity(), [], null, 0, Validators))).TestObject;
        Assert.That(BftBlockHashing.CalculateOnChainHash(genesis, Codec), Is.EqualTo(BftBlockHashing.HashAsIs(genesis)));
    }

    [Test]
    public void CommittersAreRecoveredFromSeals()
    {
        List<PrivateKey> keys = QbftTestData.Keys(3);
        BlockHeader unsealed = Header(2, []);
        ValueHash256 digest = BftBlockHashing.CalculateCommitSealDigest(unsealed, Codec);
        EthereumEcdsa ecdsa = new(1);
        Signature[] seals = [ecdsa.Sign(keys[0], in digest), ecdsa.Sign(keys[1], in digest), ecdsa.Sign(keys[2], in digest)];
        BlockHeader sealedHeader = Header(2, seals);

        Address[] committers = BftBlockHashing.RecoverCommitters(sealedHeader, Codec.Decode(sealedHeader.ExtraData), Codec);

        Assert.That(committers, Is.EqualTo(new[] { keys[0].Address, keys[1].Address, keys[2].Address }));
    }

    [Test]
    public void QbftHeaderHashIsTheOnChainDigestAndSurvivesRlpRoundTrip()
    {
        BftBlockHeader header = QbftTestData.ToQbftHeader(Header(4, [QbftTestData.Seal(1, 10, 0), QbftTestData.Seal(10, 1, 1)]));
        Assert.That(header.Hash!.ValueHash256, Is.EqualTo(BftBlockHashing.CalculateOnChainHash(header, Codec)));

        BftHeaderDecoder decoder = new();
        BlockHeader decoded = decoder.Decode(decoder.Encode(header).Bytes)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.InstanceOf<BftBlockHeader>());
            Assert.That(decoded.Hash, Is.EqualTo(header.Hash));
            Assert.That(decoded.Hash, Is.Not.EqualTo(Keccak.Compute(decoder.Encode(header).Bytes)), "the BFT hash is not the keccak of the full RLP");
        }
    }

    [Test]
    public void ReplaceRoundKeepsSealsAndChangesOnlyTheRound()
    {
        BftBlockInterface blockInterface = new(QbftOnlyCodecSelector.Instance);
        Block block = Build.A.Block.WithHeader(QbftTestData.ToQbftHeader(Header(0, []))).TestObject;

        Block inRound2 = blockInterface.ReplaceRound(block, 2);
        BftExtraData extraData = blockInterface.GetExtraData(inRound2.Header);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraData.Round, Is.EqualTo(2));
            Assert.That(extraData.Validators, Is.EqualTo(Validators));
            Assert.That(inRound2.Hash, Is.EqualTo(block.Hash), "on-chain hash is round independent");
            Assert.That(blockInterface.Digest(inRound2), Is.Not.EqualTo(blockInterface.Digest(block)), "digest includes the round");
        }
    }

    [Test]
    public void CreateSealedBlockWritesSealsAndRound()
    {
        BftBlockInterface blockInterface = new(QbftOnlyCodecSelector.Instance);
        Block block = Build.A.Block.WithHeader(QbftTestData.ToQbftHeader(Header(0, []))).TestObject;
        Signature[] seals = [QbftTestData.Seal(1, 10, 0), QbftTestData.Seal(10, 1, 1)];

        Block sealedBlock = blockInterface.CreateSealedBlock(block, 1, seals);
        BftExtraData extraData = blockInterface.GetExtraData(sealedBlock.Header);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraData.Seals, Is.EqualTo(seals));
            Assert.That(extraData.Round, Is.EqualTo(1));
            Assert.That(sealedBlock.Hash, Is.EqualTo(block.Hash));
        }
    }
}
