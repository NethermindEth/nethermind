// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7702;
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
    public static IEnumerable<object[]> AuthorizationTupleEncodeCases()
    {
        yield return new object[]
        {
            new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[64], 0))
        };
        yield return new object[]
        {
            new AuthorizationTuple(ulong.MaxValue, new Address(Enumerable.Range(0, 20).Select(i => (byte)0xff).ToArray()), UInt256.MaxValue, new Signature(Enumerable.Range(0, 64).Select(i => (byte)0xff).ToArray(), 1)),
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

    [Test]
    public void Decode_NonceItemListIsGreaterThan1_ThrowsRlpException()
    {
        RlpStream stream = RlpStreamWithTuplewithTwoNonces();

        AuthorizationTupleDecoder sut = new();

        Assert.That(() => sut.Decode(stream), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void DecodeValueDecoderContext_NonceItemListIsGreaterThan1_ThrowsRlpException()
    {
        RlpStream stream = RlpStreamWithTuplewithTwoNonces();

        AuthorizationTupleDecoder sut = new();
        Assert.That(() =>
        {
            Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(stream.Data);
            sut.Decode(ref decoderContext, RlpBehaviors.None);
        }
        , Throws.TypeOf<RlpException>());
    }

    private static RlpStream RlpStreamWithTuplewithTwoNonces()
    {
        ulong chainId = 0;
        Address codeAddress = Address.Zero;
        UInt256[] nonces = [0, 1];
        Signature sig = new(new byte[64], 0);
        int length =
            +Rlp.LengthOf(chainId)
            + Rlp.LengthOf(codeAddress)
            + Rlp.LengthOfSequence(Rlp.LengthOf(nonces[0]) + Rlp.LengthOf(nonces[1]))
            + Rlp.LengthOf(sig.RecoveryId)
            + Rlp.LengthOf(sig.R)
            + Rlp.LengthOf(sig.S);

        RlpStream stream = new RlpStream(Rlp.LengthOfSequence(length));
        stream.StartSequence(length);
        stream.Encode(chainId);
        stream.Encode(codeAddress);
        stream.StartSequence(Rlp.LengthOf(nonces[0]) + Rlp.LengthOf(nonces[1]));
        stream.Encode(nonces[0]);
        stream.Encode(nonces[1]);
        stream.Encode(sig.RecoveryId);
        stream.Encode(sig.R);
        stream.Encode(sig.S);
        stream.Position = 0;
        return stream;
    }
}
