// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Test.ModuleTests;

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
                )
            ).SetName("WithCertificates");

            yield return new TestCaseData(
                new SyncInfo(
                    new QuorumCertificate(
                        new BlockRoundInfo(Hash256.Zero, 1, 1),
                        [],
                        0
                    ),
                    new TimeoutCertificate(1, [], 0)
                )
            ).SetName("EmptyCertificates");

            yield return new TestCaseData(
                new SyncInfo(
                    new QuorumCertificate(
                        new BlockRoundInfo(Hash256.Zero, ulong.MaxValue, long.MaxValue),
                        [],
                        ulong.MaxValue
                    ),
                    new TimeoutCertificate(ulong.MaxValue, [], ulong.MaxValue)
                )
            ).SetName("MaxValues");
        }
    }

    [TestCaseSource(nameof(SyncInfoCases))]
    public void EncodeDecode_RoundTrip_Matches_AllFields(SyncInfo syncInfo)
    {
        SyncInfoDecoder decoder = new();

        Rlp encoded = decoder.Encode(syncInfo);
        RlpReader decoderContext = new(encoded.Bytes);
        SyncInfo decoded = decoder.Decode(ref decoderContext);

        Assert.That(decoded, Is.EqualTo(syncInfo).UsingXdcComparer());
    }

    [Test]
    public void EncodeToStream_RoundTrip_Matches_AllFields()
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
        byte[] bytes = new byte[decoder.GetLength(syncInfo, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        decoder.Encode(ref writer, syncInfo);

        RlpReader decoderContext = new(bytes);
        SyncInfo decoded = decoder.Decode(ref decoderContext);

        Assert.That(decoded, Is.EqualTo(syncInfo).UsingXdcComparer());
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

        SyncInfoDecoder decoder = new();
        Rlp encoded = decoder.Encode(syncInfo);

        int expectedTotal = decoder.GetLength(syncInfo, RlpBehaviors.None);
        Assert.That(encoded.Bytes.Length, Is.EqualTo(expectedTotal),
            "Encoded total length should match GetLength().");
    }

    [Test]
    public void Encode_Null_ReturnsEmptySequence()
    {
        SyncInfoDecoder decoder = new();

        Rlp encoded = decoder.Encode((SyncInfo)null!);

        Assert.That(encoded, Is.EqualTo(Rlp.OfEmptyList));
    }

    [Test]
    public void Decode_Null_ReturnsNull()
    {
        SyncInfoDecoder decoder = new();
        RlpReader context = new(Rlp.OfEmptyList.Bytes);
        SyncInfo decoded = decoder.Decode(ref context);

        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void Decode_EmptyByteArray_RlpReader_ReturnsNull()
    {
        SyncInfoDecoder decoder = new();
        RlpReader decoderContext = new(Rlp.OfEmptyList.Bytes);

        SyncInfo decoded = decoder.Decode(ref decoderContext);

        Assert.That(decoded, Is.Null);
    }

}
