// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Dynamics
{
    [Test]
    public void String()
    {
        AbiSignature<string> signature = new AbiSignature("f")
            .With(
                AbiType.String
            );

        byte[] expectedMethodId = Bytes.FromHexString("91e145ef");
        Assert.That(signature.MethodId(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, "hello");

        byte[] expected = Bytes.FromHexString(
            "91e145ef" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Location of the data part of the first parameter
            "0000000000000000000000000000000000000000000000000000000000000005" + // Data part of the first argument
            "68656c6c6f000000000000000000000000000000000000000000000000000000"); // Contents of the first argument, `"hello"`

        Assert.That(encoded, Is.EqualTo(expected));

        string decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo("hello"));
    }

    [Test]
    public void PairOfStrings()
    {
        AbiSignature<(string, string)> signature = new AbiSignature("f")
            .With(
                AbiType.String,
                AbiType.String
            );

        byte[] expectedMethodId = Bytes.FromHexString("0x18159cfb");
        Assert.That(signature.MethodId(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, ("hello", "world"));

        byte[] expected = Bytes.FromHexString(
            "0x18159cfb" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first string
            "68656c6c6f000000000000000000000000000000000000000000000000000000" + // "hello"
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of second string
            "776f726c64000000000000000000000000000000000000000000000000000000"); // "world"

        Assert.That(encoded, Is.EqualTo(expected));

        (string, string) decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(("hello", "world")));
    }

    [Test]
    public void ThreeStrings()
    {
        AbiSignature<(string, string, string)> signature = new AbiSignature("f")
            .With(
                AbiType.String,
                AbiType.String,
                AbiType.String
            );

        byte[] expectedMethodId = Bytes.FromHexString("e18744f7");
        Assert.That(signature.MethodId(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, ("hello", "my", "friends"));

        byte[] expected = Bytes.FromHexString(
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

        Assert.That(encoded, Is.EqualTo(expected));

        (string, string, string) decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(("hello", "my", "friends")));
    }

    [Test]
    public void ArrayOfBools()
    {
        AbiSignature<bool[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.Bool)
            );

        byte[] expectedMethodId = Bytes.FromHexString("da095a04");
        Assert.That(signature.MethodId(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, [true, false, true]);

        byte[] expected = Bytes.FromHexString(
            "da095a04" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element, `true`
            "0000000000000000000000000000000000000000000000000000000000000000" + // Second element, `false`
            "0000000000000000000000000000000000000000000000000000000000000001"); // Third element, `true`

        Assert.That(encoded, Is.EqualTo(expected));

        bool[] decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(new[] { true, false, true }));
    }

    [Test]
    public void ArrayOfStrings()
    {
        AbiSignature<string[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.String)
            );

        byte[] expectedMethodId = Bytes.FromHexString("e9cc8780");
        Assert.That(signature.MethodId(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, ["hello", "my", "friends"]);

        byte[] expected = Bytes.FromHexString(
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

        Assert.That(encoded, Is.EqualTo(expected));

        string[] decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(new[] { "hello", "my", "friends" }));
    }
}
