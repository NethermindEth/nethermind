// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class QuorumCertificateDecoderTests
{

    public static IEnumerable QuorumCertificateCases
    {
        get
        {
            yield return new TestCaseData(new QuorumCert(new BlockInfo(Hash256.Zero, 1, 1), [new Signature(new byte[64], 0), new Signature(new byte[64], 0), new Signature(new byte[64], 0)], 0));
            yield return new TestCaseData(new QuorumCert(new BlockInfo(Hash256.Zero, 1, 1), [], 0));
            yield return new TestCaseData(new QuorumCert(new BlockInfo(Hash256.Zero, ulong.MaxValue, long.MaxValue), [], int.MaxValue));
        }
    }

    [TestCaseSource(nameof(QuorumCertificateCases))]
    public void Encode_DifferentValues_IsEquivalentAfterReencoding(QuorumCert quorumCert)
    {
        QuorumCertificateDecoder decoder = new ();
        RlpStream stream = new RlpStream(decoder.GetLength(quorumCert));
        decoder.Encode(stream, quorumCert);
        stream.Position = 0;
        QuorumCert decoded = decoder.Decode(stream);

        decoded.Should().BeEquivalentTo(quorumCert);
    }

}
