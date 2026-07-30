// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class VoteDecoderTests
{
    public static IEnumerable VoteCases
    {
        get
        {
            yield return new TestCaseData(
                new Vote(
                    new BlockRoundInfo(Hash256.Zero, 1, 1),
                    0,
                    new Signature(new byte[64], 0)
                )
            ).SetName("BasicVote");

            yield return new TestCaseData(
                new Vote(
                    new BlockRoundInfo(Hash256.Zero, ulong.MaxValue, long.MaxValue),
                    ulong.MaxValue,
                    new Signature(new byte[64], 0)
                )
            ).SetName("MaxValues");
        }
    }

    [TestCaseSource(nameof(VoteCases))]
    public void EncodeDecode_RoundTrip_Matches_AllFields(Vote vote)
    {
        VoteDecoder decoder = new();

        Rlp encoded = decoder.Encode(vote);
        RlpReader decoderContext = new(encoded.Bytes);
        Vote decoded = decoder.Decode(ref decoderContext);

        Assert.That(decoded, Is.EqualTo(vote).UsingXdcComparer(compareSigner: false));
    }

    [Test]
    public void EncodeToStream_RoundTrip_Matches_AllFields()
    {
        Vote vote = new(
            new BlockRoundInfo(Hash256.Zero, 1, 1),
            0,
            new Signature(new byte[64], 0)
        );

        VoteDecoder decoder = new();
        byte[] bytes = new byte[decoder.GetLength(vote, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        decoder.Encode(ref writer, vote);

        RlpReader decoderContext = new(bytes);
        Vote decoded = decoder.Decode(ref decoderContext);

        Assert.That(decoded, Is.EqualTo(vote).UsingXdcComparer(compareSigner: false));
    }

    [Test]
    public void TotalLength_Equals_GetLength()
    {
        Vote vote = new(
            new BlockRoundInfo(Hash256.Zero, 42, 42),
            10,
            new Signature(new byte[64], 0)
        );

        VoteDecoder decoder = new();
        Rlp encoded = decoder.Encode(vote);

        int expectedTotal = decoder.GetLength(vote, RlpBehaviors.None);
        Assert.That(encoded.Bytes.Length, Is.EqualTo(expectedTotal),
            "Encoded total length should match GetLength().");
    }

    [Test]
    public void Encode_ForSealing_Omits_Signature()
    {
        Vote vote = new(
            new BlockRoundInfo(Hash256.Zero, 1, 1),
            0,
            new Signature(new byte[64], 0)
        );

        VoteDecoder decoder = new();

        Rlp normalEncoded = decoder.Encode(vote, RlpBehaviors.None);

        Rlp sealingEncoded = decoder.Encode(vote, RlpBehaviors.ForSealing);

        Assert.That(sealingEncoded.Bytes.Length, Is.LessThan(normalEncoded.Bytes.Length),
            "ForSealing encoding should be shorter as it omits the signature.");

        RlpReader context = new(sealingEncoded.Bytes);
        Vote decoded = decoder.Decode(ref context, RlpBehaviors.ForSealing);

        Assert.That(decoded.Signature, Is.Null,
            "ForSealing decoding should not contain Signature field.");
        Assert.That(decoded.ProposedBlockInfo.Round, Is.EqualTo(vote.ProposedBlockInfo.Round));
        Assert.That(decoded.GapNumber, Is.EqualTo(vote.GapNumber));
    }

    [Test]
    public void Encode_Null_ReturnsEmptySequence()
    {
        VoteDecoder decoder = new();

        Rlp encoded = decoder.Encode((Vote)null!);

        Assert.That(encoded, Is.EqualTo(Rlp.OfEmptyList));
    }

    [Test]
    public void Decode_Null_ReturnsNull()
    {
        VoteDecoder decoder = new();
        RlpReader context = new(Rlp.OfEmptyList.Bytes);
        Vote decoded = decoder.Decode(ref context);

        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Decode_EmptyByteArray_RlpReader_ReturnsNull()
    {
        VoteDecoder decoder = new();
        RlpReader decoderContext = new(Rlp.OfEmptyList.Bytes);

        Vote decoded = decoder.Decode(ref decoderContext);

        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void GetLength_ForSealing_IsShorter()
    {
        Vote vote = new(
            new BlockRoundInfo(Hash256.Zero, 1, 1),
            0,
            new Signature(new byte[64], 0)
        );

        VoteDecoder decoder = new();

        int normalLength = decoder.GetLength(vote, RlpBehaviors.None);
        int sealingLength = decoder.GetLength(vote, RlpBehaviors.ForSealing);

        Assert.That(sealingLength, Is.LessThan(normalLength),
            "ForSealing length should be shorter as signature is omitted.");
    }

    [Test]
    public void Vote_PoolKey_ReturnsRoundAndHash()
    {
        Vote vote = new(
            new BlockRoundInfo(Hash256.Zero, 1, 100),
            10,
            new Signature(new byte[64], 0)
        );

        (ulong round, Hash256? hash) = vote.PoolKey();

        Assert.That(round, Is.EqualTo(1UL));
        Assert.That(hash, Is.Not.EqualTo(Hash256.Zero)); // Should be computed hash
    }

}
