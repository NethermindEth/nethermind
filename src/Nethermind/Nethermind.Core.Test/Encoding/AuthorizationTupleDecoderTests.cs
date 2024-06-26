// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7702;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class AuthorizationTupleDecoderTests
{
    public static IEnumerable<object[]> AuthorizationTupleEncodeCases()
    {
        yield return new object[]
        {
            new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[64], 0))
        };
        yield return new object[]
        {
            new AuthorizationTuple(ulong.MaxValue, new Address(Enumerable.Range(0, 20).Select(i => (byte)0xff).ToArray()), UInt256.MaxValue, new Signature(Enumerable.Range(0, 64).Select(i => (byte)0xff).ToArray(), int.MaxValue)),
        };
    }

    [TestCaseSource(nameof(AuthorizationTupleEncodeCases))]
    public void Encode_TupleHasValues_TupleCanBeDecodedToEquivalentTuple(AuthorizationTuple item)
    {
        AuthorizationTupleDecoder sut = new();

        RlpStream result = sut.Encode(item);
        result.Position = 0;

        sut.Decode(result).Should().BeEquivalentTo(item);
    }
}
