// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        Rlp result = sut.Encode(item);
        RlpReader ctx = new(result.Bytes);

        AuthorizationTuple decoded = sut.Decode(ref ctx)!;
        Assert.That(decoded, Is.EqualTo(item).UsingAuthorizationTupleComparer());
    }

    [Test]
    public void DecodeRlpReader_CodeAddressIsNull_ThrowsRlpException()
    {
        byte[] tuple = TupleRlpWithNull();

        AuthorizationTupleDecoder sut = new();
        Assert.That(() =>
        {
            RlpReader decoderContext = new(tuple);
            sut.Decode(ref decoderContext, RlpBehaviors.None);
        }, Throws.TypeOf<RlpException>());
    }

    public static IEnumerable<byte[]> WrongSizeFieldsEncodedCases()
    {
        yield return TupleRlp(
            //Wrong chain size
            Enumerable.Range(0, 33).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes.ToArray(),
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlp(
            //Wrong address size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 19).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlp(
            //Wrong address size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 21).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlp(
            //Wrong nonce size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes.ToArray(),
            Enumerable.Range(0, 9).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlp(
            //Wrong yParity size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes.ToArray(),
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 2).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlp(
            //Wrong R size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes.ToArray(),
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 33).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray()
            );

        yield return TupleRlp(
            //Wrong S size
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Address.Zero.Bytes.ToArray(),
            Enumerable.Range(0, 8).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 1).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 32).Select(static i => (byte)0xFF).ToArray(),
            Enumerable.Range(0, 33).Select(static i => (byte)0xFF).ToArray()
            );
    }

    [TestCaseSource(nameof(WrongSizeFieldsEncodedCases))]
    public void Encode_TupleHasFieldsOutsideBoundaries_ThrowsRlpException(byte[] badEncoding)
    {
        AuthorizationTupleDecoder sut = new();

        Assert.That(() =>
        {
            RlpReader ctx = new(badEncoding);
            sut.Decode(ref ctx, RlpBehaviors.None);
        }, Throws.InstanceOf<RlpException>());
    }

    private static byte[] TupleRlpWithNull()
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
        byte[] rlp = new byte[Rlp.LengthOfSequence(length)];
        RlpWriter writer = new(rlp);
        writer.StartSequence(length);
        writer.Encode(1);
        writer.Encode(codeAddress);
        writer.Encode(0);
        writer.Encode(sig.RecoveryId);
        writer.Encode(sig.R);
        writer.Encode(sig.S);
        return rlp;
    }

    private static byte[] TupleRlp(byte[] chainId, byte[] address, byte[] nonce, byte[] yParity, byte[] r, byte[] s)
    {
        int length =
            +Rlp.LengthOf(chainId)
            + Rlp.LengthOf(address)
            + Rlp.LengthOf(nonce)
            + Rlp.LengthOf(yParity)
            + Rlp.LengthOf(r)
            + Rlp.LengthOf(s);
        byte[] rlp = new byte[Rlp.LengthOfSequence(length)];
        RlpWriter writer = new(rlp);
        writer.StartSequence(length);
        writer.Encode(chainId);
        writer.Encode(address);
        writer.Encode(nonce);
        writer.Encode(yParity);
        writer.Encode(r);
        writer.Encode(s);
        return rlp;
    }
}
