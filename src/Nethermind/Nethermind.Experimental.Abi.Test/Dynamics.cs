// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Dynamics
{
    [Test]
    public void String()
    {
        var signature = new AbiSignature("f")
            .With(
                AbiType.String
            );

        var expectedMethodId = Bytes.FromHexString("91e145ef");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = AbiCodec.Encode(signature, "hello");

        var expected = Bytes.FromHexString(
            "91e145ef" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Location of the data part of the first parameter
            "0000000000000000000000000000000000000000000000000000000000000005" + // Data part of the first argument
            "68656c6c6f000000000000000000000000000000000000000000000000000000"); // Contents of the first argument, `"hello"`

        encoded.Should().BeEquivalentTo(expected);

        string decoded = AbiCodec.Decode(signature, encoded);
        decoded.Should().Be("hello");
    }

    [Test]
    public void PairOfStrings()
    {
        var signature = new AbiSignature("f")
            .With(
                AbiType.String,
                AbiType.String
            );

        var expectedMethodId = Bytes.FromHexString("0x18159cfb");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = AbiCodec.Encode(signature, ("hello", "world"));

        var expected = Bytes.FromHexString(
            "0x18159cfb" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first string
            "68656c6c6f000000000000000000000000000000000000000000000000000000" + // "hello"
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of second string
            "776f726c64000000000000000000000000000000000000000000000000000000"); // "world"

        encoded.Should().BeEquivalentTo(expected);

        (string a, string b) = AbiCodec.Decode(signature, encoded);
        a.Should().Be("hello");
        b.Should().Be("world");
    }

    [Test]
    public void ThreeStrings()
    {
        var signature = new AbiSignature("f")
            .With(
                AbiType.String,
                AbiType.String,
                AbiType.String
            );

        var expectedMethodId = Bytes.FromHexString("e18744f7");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = AbiCodec.Encode(signature, ("hello", "my", "friends"));

        var expected = Bytes.FromHexString(
            "e18744f7" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first string
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to second string
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to third string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first string
            "68656c6c6f000000000000000000000000000000000000000000000000000000" + // "hello"
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of second string
            "6d79000000000000000000000000000000000000000000000000000000000000" + // "my"
            "0000000000000000000000000000000000000000000000000000000000000007" + // Length of third string
            "667269656e647300000000000000000000000000000000000000000000000000");  // "friends"

        encoded.Should().BeEquivalentTo(expected);

        (string a, string b, string c) = AbiCodec.Decode(signature, encoded);
        a.Should().Be("hello");
        b.Should().Be("my");
        c.Should().Be("friends");
    }

    [Test]
    public void ArrayOfBools()
    {
        var signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.Bool)
            );

        var expectedMethodId = Bytes.FromHexString("da095a04");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = AbiCodec.Encode(signature, [true, false, true]);

        var expected = Bytes.FromHexString(
            "da095a04" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element, `true`
            "0000000000000000000000000000000000000000000000000000000000000000" + // Second element, `false`
            "0000000000000000000000000000000000000000000000000000000000000001"); // Third element, `true`

        encoded.Should().BeEquivalentTo(expected);

        bool[] decoded = AbiCodec.Decode(signature, encoded);
        decoded.Length.Should().Be(3);
        decoded[0].Should().Be(true);
        decoded[1].Should().Be(false);
        decoded[2].Should().Be(true);
    }

    [Test]
    public void ArrayOfStrings()
    {
        var signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.String)
            );

        var expectedMethodId = Bytes.FromHexString("e9cc8780");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = AbiCodec.Encode(signature, ["hello", "my", "friends"]);

        var expected = Bytes.FromHexString(
            "e9cc8780" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first string
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to second string
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to third string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Data part of the first argument
            "68656c6c6f000000000000000000000000000000000000000000000000000000" + // Contents of the first argument, "hello"
            "0000000000000000000000000000000000000000000000000000000000000002" + // Data part of the second argument
            "6d79000000000000000000000000000000000000000000000000000000000000" + // Contents of the second argument, "my"
            "0000000000000000000000000000000000000000000000000000000000000007" + // Data part of the third argument
            "667269656e647300000000000000000000000000000000000000000000000000"); // Contents of the third argument, "friends"

        encoded.Should().BeEquivalentTo(expected);

        string[] decoded = AbiCodec.Decode(signature, encoded);
        decoded.Length.Should().Be(3);
        decoded[0].Should().Be("hello");
        decoded[1].Should().Be("my");
        decoded[2].Should().Be("friends");
    }
}
