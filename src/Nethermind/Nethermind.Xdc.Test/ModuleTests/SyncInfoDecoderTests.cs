// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class SyncInfoDecoderTests
{
    public static IEnumerable SyncInfoCases
    {
        get
        {
            yield return new TestCaseData(
                new SyncInfo(
                    new QuorumCertificate(
                        new BlockRoundInfo(Hash256.Zero, 1, 1),
                        [new Signature(new byte[64], 0), new Signature(new byte[64], 0)],
                        0
                    ),
                    new TimeoutCertificate(
                        1,
                        [new Signature(new byte[64], 0), new Signature(new byte[64], 0)],
                        0
                    )
                ),
                true
            );

            yield return new TestCaseData(
                new SyncInfo(
                    new QuorumCertificate(
                        new BlockRoundInfo(Hash256.Zero, 1, 1),
                        [],
                        0
                    ),
                    new TimeoutCertificate(1, [], 0)
                ),
                false
            );

            yield return new TestCaseData(
                new SyncInfo(
                    new QuorumCertificate(
                        new BlockRoundInfo(Hash256.Zero, ulong.MaxValue, long.MaxValue),
                        [],
                        ulong.MaxValue
                    ),
                    new TimeoutCertificate(ulong.MaxValue, [], ulong.MaxValue)
                ),
                true
            );
        }
    }

    [TestCaseSource(nameof(SyncInfoCases))]
    public void EncodeDecode_RoundTrip_Matches_AllFields(SyncInfo syncInfo, bool useRlpStream)
    {
        var decoder = new SyncInfoDecoder();

        Rlp encoded = decoder.Encode(syncInfo);
        var stream = new RlpStream(encoded.Bytes);
        SyncInfo decoded;

        if (useRlpStream)
        {
            decoded = decoder.Decode(stream);
        }
        else
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
            decoded = decoder.Decode(ref decoderContext);
        }

        decoded.Should().BeEquivalentTo(syncInfo);
    }

    [Test]
    public void Encode_UseBothRlpStreamAndValueDecoderContext_IsEquivalentAfterReencoding()
    {
        SyncInfo syncInfo = new(
            new QuorumCertificate(
                new BlockRoundInfo(Hash256.Zero, 1, 1),
                [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)],
                0
            ),
            new TimeoutCertificate(
                1,
                [new Signature(new byte[64], 0), new Signature(new byte[64], 0)],
                0
            )
        );

        SyncInfoDecoder decoder = new();
        RlpStream stream = new RlpStream(decoder.GetLength(syncInfo));
        decoder.Encode(stream, syncInfo);
        stream.Position = 0;

        // Decode with RlpStream
        SyncInfo decodedStream = decoder.Decode(stream);
        stream.Position = 0;

        // Decode with ValueDecoderContext
        Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
        SyncInfo decodedContext = decoder.Decode(ref decoderContext);

        // Both should be equivalent to original
        decodedStream.Should().BeEquivalentTo(syncInfo);
        decodedContext.Should().BeEquivalentTo(syncInfo);
        decodedStream.Should().BeEquivalentTo(decodedContext);
    }

    [Test]
    public void TotalLength_Equals_GetLength()
    {
        SyncInfo syncInfo = new(
            new QuorumCertificate(
                new BlockRoundInfo(Hash256.Zero, 42, 42),
                [new Signature(new byte[64], 0)],
                10
            ),
            new TimeoutCertificate(
                41,
                [new Signature(new byte[64], 1)],
                10
            )
        );

        var decoder = new SyncInfoDecoder();
        Rlp encoded = decoder.Encode(syncInfo);

        int expectedTotal = decoder.GetLength(syncInfo, RlpBehaviors.None);
        Assert.That(encoded.Bytes.Length, Is.EqualTo(expectedTotal),
            "Encoded total length should match GetLength().");
    }

    [Test]
    public void Encode_Null_ReturnsEmptySequence()
    {
        var decoder = new SyncInfoDecoder();

        Rlp encoded = decoder.Encode(null!);

        Assert.That(encoded, Is.EqualTo(Rlp.OfEmptyList));
    }

    [Test]
    public void Decode_Null_ReturnsNull()
    {
        var decoder = new SyncInfoDecoder();
        var stream = new RlpStream(Rlp.OfEmptyList.Bytes);

        SyncInfo decoded = decoder.Decode(stream);

        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Decode_EmptyByteArray_ValueDecoderContext_ReturnsNull()
    {
        var decoder = new SyncInfoDecoder();
        Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(Rlp.OfEmptyList.Bytes);

        SyncInfo decoded = decoder.Decode(ref decoderContext);

        Assert.That(decoded, Is.Null);
    }
}
