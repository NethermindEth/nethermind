using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Experimental.Abi.V2;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

public class Examples
{
    [Test]
    public void Bar()
    {
        var signature = new AbiSignature("bar")
            .Arg(AbiType.Array(AbiType.BytesM(3), length: 2));

        var expectedMethodId = Bytes.FromHexString("0xfce353f6");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = V2.Abi.Encode(signature, [[(byte)'a', (byte)'b', (byte)'c'], [(byte)'d', (byte)'e', (byte)'f']]);

        var expected = Bytes.FromHexString("0xfce353f6" + // `MethodId`
                                           "6162630000000000000000000000000000000000000000000000000000000000" + // "abc"
                                           "6465660000000000000000000000000000000000000000000000000000000000"); // "def"

        encoded.Should().BeEquivalentTo(expected);
        var args = V2.Abi.Decode(signature, encoded);
        args.Should().BeEquivalentTo(new byte[][] { [(byte)'a', (byte)'b', (byte)'c'], [(byte)'d', (byte)'e', (byte)'f'] });
    }

    [Test]
    public void Baz()
    {
        var signature = new AbiSignature("baz")
            .Arg(AbiType.UInt32)
            .Arg(AbiType.Bool);

        var expectedMethodId = Bytes.FromHexString("0xcdcd77c0");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = V2.Abi.Encode(signature, 69u, true);

        var expected = Bytes.FromHexString("0xcdcd77c0" + // `MethodId`
                                           "0000000000000000000000000000000000000000000000000000000000000045" + // `69`
                                           "0000000000000000000000000000000000000000000000000000000000000001"); // `true`
        encoded.Should().BeEquivalentTo(expected);

        (UInt32 a, bool b) = V2.Abi.Decode(signature, encoded);
        a.Should().Be(69u);
        b.Should().Be(true);
    }
}
