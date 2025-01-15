// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;

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
            new Address(Enumerable.Range(0, 20).Select(static i => (byte)0xff).ToArray()),
            ulong.MaxValue,
            new Signature(Enumerable.Range(0, 64).Select(static i => (byte)0xff).ToArray(), 1));
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

    public static IEnumerable<RlpStream> WrongSizeFieldsEncodedCases()
    {
        yield return TupleRlpStream(
            //Wrong chain size
            Enumerable.Range(0, 33).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes,
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlpStream(
            //Wrong address size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 19).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlpStream(
            //Wrong address size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 21).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlpStream(
            //Wrong nonce size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes,
            Enumerable.Range(0, 9).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlpStream(
            //Wrong yParity size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes,
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 2).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlpStream(
            //Wrong R size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes,
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 33).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlpStream(
            //Wrong S size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes,
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 33).Select(static i => (byte)0xFF).ToArray()
            );
    }

    [TestCaseSource(nameof(WrongSizeFieldsEncodedCases))]
    public void Encode_TupleHasFieldsOutsideBoundaries_ThrowsRlpException(RlpStream badEncoding)
    {
        AuthorizationTupleDecoder sut = new();

        Assert.That(() => sut.Decode(badEncoding, RlpBehaviors.None), Throws.InstanceOf<RlpException>());
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

    private static RlpStream TupleRlpStream(byte[] chainId, byte[] address, byte[] nonce, byte[] yParity, byte[] r, byte[] s)
    {
        int length =
            +Rlp.LengthOf(chainId)
            + Rlp.LengthOf(address)
            + Rlp.LengthOf(nonce)
            + Rlp.LengthOf(yParity)
            + Rlp.LengthOf(r)
            + Rlp.LengthOf(s);
        RlpStream stream = new RlpStream(Rlp.LengthOfSequence(length));
        stream.StartSequence(length);
        stream.Encode(chainId);
        stream.Encode(address);
        stream.Encode(nonce);
        stream.Encode(yParity);
        stream.Encode(r);
        stream.Encode(s);
        stream.Position = 0;
        return stream;
    }
}
