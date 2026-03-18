// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections;

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
                ),
                true
            );

            yield return new TestCaseData(
                new Vote(
                    new BlockRoundInfo(Hash256.Zero, ulong.MaxValue, long.MaxValue),
                    ulong.MaxValue,
                    new Signature(new byte[64], 0)
                ),
                true
            );

            yield return new TestCaseData(
                new Vote(
                    new BlockRoundInfo(Hash256.Zero, 1, 1),
                    0,
                    new Signature(new byte[64], 0)
                ),
                false
            );
        }
    }

    [TestCaseSource(nameof(VoteCases))]
    public void EncodeDecode_RoundTrip_Matches_AllFields(Vote vote, bool useRlpStream)
    {
        var decoder = new VoteDecoder();

        Rlp encoded = decoder.Encode(vote);
        var stream = new RlpStream(encoded.Bytes);
        Vote decoded;

        if (useRlpStream)
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
            decoded = decoder.Decode(ref decoderContext);
        }
        else
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
            decoded = decoder.Decode(ref decoderContext);
        }

        decoded.Should().BeEquivalentTo(vote, options => options.Excluding(v => v.Signer));
    }

    [Test]
    public void Encode_UseBothRlpStreamAndValueDecoderContext_IsEquivalentAfterReencoding()
    {
        Vote vote = new(
            new BlockRoundInfo(Hash256.Zero, 1, 1),
            0,
            new Signature(new byte[64], 0)
        );

        VoteDecoder decoder = new();
        RlpStream stream = new RlpStream(decoder.GetLength(vote));
        decoder.Encode(stream, vote);
        stream.Position = 0;

        Rlp.ValueDecoderContext streamCtx = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
        Vote decodedStream = decoder.Decode(ref streamCtx);

        Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
        Vote decodedContext = decoder.Decode(ref decoderContext);

        decodedStream.Should().BeEquivalentTo(vote, options => options.Excluding(v => v.Signer));
        decodedContext.Should().BeEquivalentTo(vote, options => options.Excluding(v => v.Signer));
        decodedStream.Should().BeEquivalentTo(decodedContext);
    }

    [Test]
    public void TotalLength_Equals_GetLength()
    {
        Vote vote = new(
            new BlockRoundInfo(Hash256.Zero, 42, 42),
            10,
            new Signature(new byte[64], 0)
        );

        var decoder = new VoteDecoder();
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

        var decoder = new VoteDecoder();

        Rlp normalEncoded = decoder.Encode(vote, RlpBehaviors.None);

        Rlp sealingEncoded = decoder.Encode(vote, RlpBehaviors.ForSealing);

        Assert.That(sealingEncoded.Bytes.Length, Is.LessThan(normalEncoded.Bytes.Length),
            "ForSealing encoding should be shorter as it omits the signature.");

        Vote decoded = decoder.Decode((ReadOnlySpan<byte>)sealingEncoded.Bytes, RlpBehaviors.ForSealing);

        Assert.That(decoded.Signature, Is.Null,
            "ForSealing decoding should not contain Signature field.");
        Assert.That(decoded.ProposedBlockInfo.Round, Is.EqualTo(vote.ProposedBlockInfo.Round));
        Assert.That(decoded.GapNumber, Is.EqualTo(vote.GapNumber));
    }

    [Test]
    public void Encode_Null_ReturnsEmptySequence()
    {
        var decoder = new VoteDecoder();

        Rlp encoded = decoder.Encode(null!);

        Assert.That(encoded, Is.EqualTo(Rlp.OfEmptyList));
    }

    [Test]
    public void Decode_Null_ReturnsNull()
    {
        var decoder = new VoteDecoder();
        Vote decoded = decoder.Decode((ReadOnlySpan<byte>)Rlp.OfEmptyList.Bytes);

        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Decode_EmptyByteArray_ValueDecoderContext_ReturnsNull()
    {
        var decoder = new VoteDecoder();
        Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(Rlp.OfEmptyList.Bytes);

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

        var decoder = new VoteDecoder();

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

        var (round, hash) = vote.PoolKey();

        Assert.That(round, Is.EqualTo(1UL));
        Assert.That(hash, Is.Not.EqualTo(Hash256.Zero)); // Should be computed hash
    }
}
