// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class TimeoutCertificateDecoderTests
{
    public static IEnumerable TcCases
    {
        get
        {
            yield return new TestCaseData(new TimeoutCertificate(1, [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0))
                .SetName("WithSignatures");
            yield return new TestCaseData(new TimeoutCertificate(1, [], 0))
                .SetName("EmptySignatures");
        }
    }

    [TestCaseSource(nameof(TcCases))]
    public void EncodeDecode_RoundTrip_Matches_AllFields(TimeoutCertificate tc)
    {
        TimeoutCertificateDecoder decoder = new();

        Rlp encoded = decoder.Encode(tc);
        RlpReader decoderContext = new(encoded.Bytes);
        TimeoutCertificate decoded = decoder.Decode(ref decoderContext);

        Assert.That(decoded, Is.EqualTo(tc).UsingXdcComparer());
    }
}
