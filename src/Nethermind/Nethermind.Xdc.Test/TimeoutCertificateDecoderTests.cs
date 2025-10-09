// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using System.Collections;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class TimeoutCertificateDecoderTests
{
    public static IEnumerable TcCases
    {
        get
        {
            yield return new TestCaseData(new TimeoutCertificate(1, [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0), true);
            yield return new TestCaseData(new TimeoutCertificate(1, [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0), false);
            yield return new TestCaseData(new TimeoutCertificate(1, [], 0), true);
            yield return new TestCaseData(new TimeoutCertificate(1, [], 0), false);
        }
    }
    [TestCaseSource(nameof(TcCases))]
    public void EncodeDecode_RoundTrip_Matches_AllFields(TimeoutCertificate tc, bool useRlpStream)
    {
        var decoder = new TimeoutCertificateDecoder();

        Rlp encoded = decoder.Encode(tc);
        var stream = new RlpStream(encoded.Bytes);
        TimeoutCertificate decoded;
        if (useRlpStream)
        {
            decoded = decoder.Decode(stream);
        }
        else
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(stream.Data.AsSpan());
            decoded = decoder.Decode(ref decoderContext);
        }

        decoded.Should().BeEquivalentTo(tc);
    }
}
