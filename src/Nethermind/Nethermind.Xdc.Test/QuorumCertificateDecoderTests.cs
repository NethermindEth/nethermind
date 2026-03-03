// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class QuorumCertificateDecoderTests
{

    public static IEnumerable QuorumCertificateCases
    {
        get
        {
            yield return new TestCaseData(new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 1, 1), [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0));
            yield return new TestCaseData(new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 1, 1), [], 0));
            yield return new TestCaseData(new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, ulong.MaxValue, long.MaxValue), [], int.MaxValue));
        }
    }

    [TestCaseSource(nameof(QuorumCertificateCases))]
    public void Encode_DifferentValues_IsEquivalentAfterReencoding(QuorumCertificate quorumCert)
    {
        QuorumCertificateDecoder decoder = new();
        RlpStream stream = new RlpStream(decoder.GetLength(quorumCert));
        decoder.Encode(stream, quorumCert);
        Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
        QuorumCertificate decoded = decoder.Decode(ref ctx);

        decoded.Should().BeEquivalentTo(quorumCert);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Encode_UseBothRlpStreamAndValueDecoderContext_IsEquivalentAfterReencoding(bool useRlpStream)
    {
        QuorumCertificate quorumCert = new(new BlockRoundInfo(Hash256.Zero, 1, 1), [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0);
        QuorumCertificateDecoder decoder = new();
        RlpStream stream = new RlpStream(decoder.GetLength(quorumCert));
        decoder.Encode(stream, quorumCert);
        QuorumCertificate decoded;
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

        decoded.Should().BeEquivalentTo(quorumCert);
    }

}
