using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Experimental.Abi.V2;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Examples
{
    [Test]
    public void Bar()
    {
        var signature = new AbiSignature("bar")
            .With(
                AbiType.Array(AbiType.BytesM(3), length: 2));

        var expectedMethodId = Bytes.FromHexString("0xfce353f6");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = V2.Abi.Encode(signature, [[(byte)'a', (byte)'b', (byte)'c'], [(byte)'d', (byte)'e', (byte)'f']]);

        var expected = Bytes.FromHexString(
            "0xfce353f6" + // `MethodId`
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
            .With(
                AbiType.UInt32,
                AbiType.Bool);

        var expectedMethodId = Bytes.FromHexString("0xcdcd77c0");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = V2.Abi.Encode(signature, (69u, true));

        var expected = Bytes.FromHexString(
            "0xcdcd77c0" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000045" + // `69`
            "0000000000000000000000000000000000000000000000000000000000000001"); // `true`
        encoded.Should().BeEquivalentTo(expected);

        (UInt32 a, bool b) = V2.Abi.Decode(signature, encoded);
        a.Should().Be(69u);
        b.Should().Be(true);
    }

    [Test]
    public void Sam()
    {
        var signature = new AbiSignature("sam")
            .With(
                AbiType.Bytes,
                AbiType.Bool,
                AbiType.Array(AbiType.UInt));

        var expectedMethodId = Bytes.FromHexString("0xa5643bf2");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = V2.Abi.Encode(signature, ("dave"u8.ToArray(), true, [1, 2, 3]));

        var expected = Bytes.FromHexString(
            "0xa5643bf2" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000060" + // Location of the data part of the first parameter
            "0000000000000000000000000000000000000000000000000000000000000001" + // Second argument, `true`
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Location of the data part of the third parameter
            "0000000000000000000000000000000000000000000000000000000000000004" + // Data part of the first argument
            "6461766500000000000000000000000000000000000000000000000000000000" + // Contents of the first argument, `"dave"`
            "0000000000000000000000000000000000000000000000000000000000000003" + // Data part of the third argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element of the third argument, `1`
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second element of the third argument, `2`
            "0000000000000000000000000000000000000000000000000000000000000003"); // Third element of the third argument, `3`
        encoded.Should().BeEquivalentTo(expected);

        (byte[] a, bool b, UInt256[] c) = V2.Abi.Decode(signature, encoded);
        a.Should().BeEquivalentTo("dave"u8.ToArray());
        b.Should().Be(true);
        c.Should().BeEquivalentTo(new UInt256[] { 1, 2, 3 });
    }
}
