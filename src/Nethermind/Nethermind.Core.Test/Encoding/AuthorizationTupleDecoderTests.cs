// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NSubstitute.ExceptionExtensions;
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
    public static IEnumerable<AuthorizationTuple> AuthorizationTupleEncodeCases()
    {
        yield return new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[64], 0));
        yield return new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[64], 0));
        yield return new AuthorizationTuple(
            ulong.MaxValue,
            new Address(Enumerable.Range(0, 20).Select(i => (byte)0xff).ToArray()),
            ulong.MaxValue,
            new Signature(Enumerable.Range(0, 64).Select(i => (byte)0xff).ToArray(), 1));
    }

    [TestCaseSource(nameof(AuthorizationTupleEncodeCases))]
    public void Encode_TupleHasValues_TupleCanBeDecodedToEquivalentTuple(AuthorizationTuple item)
    {
        AuthorizationTupleDecoder sut = new();

        RlpStream result = sut.Encode(item);
        result.Position = 0;

        sut.Decode(result).Should().BeEquivalentTo(item);
    }

    [Test]
    public void DecodeValueDecoderContext_CodeAddressIsNull_ThrowsRlpException()
    {
        RlpStream stream = TupleRlpStreamWithNull();

        AuthorizationTupleDecoder sut = new();
        Assert.That(() =>
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(stream.Data);
            sut.Decode(ref decoderContext, RlpBehaviors.None);
        }
        , Throws.TypeOf<RlpException>());
    }

    private static RlpStream TupleRlpStreamWithNull()
    {
        Address? codeAddress = null;
        Signature sig = new(new byte[64], 0);
        int length =
            +Rlp.LengthOf(1)
            + Rlp.LengthOf(codeAddress)
            + Rlp.LengthOf(0)
            + Rlp.LengthOf(sig.RecoveryId)
            + Rlp.LengthOf(sig.R)
            + Rlp.LengthOf(sig.S);
        RlpStream stream = new RlpStream(Rlp.LengthOfSequence(length));
        stream.StartSequence(length);
        stream.Encode(1);
        stream.Encode(codeAddress);
        stream.Encode(0);
        stream.Encode(sig.RecoveryId);
        stream.Encode(sig.R);
        stream.Encode(sig.S);
        stream.Position = 0;
        return stream;
    }
}
