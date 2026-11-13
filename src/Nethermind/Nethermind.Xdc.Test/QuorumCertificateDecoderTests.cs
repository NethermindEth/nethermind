// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Encoding;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class QuorumCertificateDecoderTests
{
    public static IEnumerable QuorumCertificateCases
    {
        get
        {
            yield return new TestCaseData(new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 1, 1), [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0))
                .SetName("WithSignatures");
            yield return new TestCaseData(new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 1, 1), [], 0))
                .SetName("EmptySignatures");
            yield return new TestCaseData(new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, ulong.MaxValue, long.MaxValue), [], int.MaxValue))
                .SetName("MaxValues");
        }
    }

    [TestCaseSource(nameof(QuorumCertificateCases))]
    public void Encode_DifferentValues_IsEquivalentAfterReencoding(QuorumCertificate quorumCert)
    {
        QuorumCertificateDecoder decoder = new();
        byte[] bytes = new byte[decoder.GetLength(quorumCert, RlpBehaviors.None)];
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        decoder.Encode(ref writer, quorumCert);
        RlpReader ctx = new(bytes);
        QuorumCertificate decoded = decoder.DecodeGuardNotNull(ref ctx);

        Assert.That(decoded, Is.EqualTo(quorumCert).UsingXdcComparer());
    }

    [Test]
    public void Encode_RlpReader_IsEquivalentAfterReencoding()
    {
        QuorumCertificate quorumCert = new(new BlockRoundInfo(Hash256.Zero, 1, 1), [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0);
        QuorumCertificateDecoder decoder = new();
        byte[] bytes = new byte[decoder.GetLength(quorumCert, RlpBehaviors.None)];
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        decoder.Encode(ref writer, quorumCert);
        RlpReader decoderContext = new(bytes);
        QuorumCertificate decoded = decoder.DecodeGuardNotNull(ref decoderContext);

        Assert.That(decoded, Is.EqualTo(quorumCert).UsingXdcComparer());
    }

    [Test]
    public void BlockRoundInfo_decodes_with_a_trailing_item()
    {
        XdcBlockInfoDecoder decoder = new();
        BlockRoundInfo info = new(Hash256.Zero, 2, 3);

        BlockRoundInfo? decoded = decoder.Decode(RlpTestHelper.WithTrailingItem(decoder.EncodeAsBytes(info)));

        Assert.That(decoded, Is.EqualTo(info).UsingXdcComparer());
    }
}
