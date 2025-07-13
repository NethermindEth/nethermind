// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

/// <remarks>
/// The tests have been provided by the ABI specification.
/// See: https://docs.soliditylang.org/en/latest/abi-spec.html#formal-specification-of-the-encoding
/// </remarks>
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

        byte[] encoded = AbiCodec.Encode(signature, [[(byte)'a', (byte)'b', (byte)'c'], [(byte)'d', (byte)'e', (byte)'f']]);

        var expected = Bytes.FromHexString(
            "0xfce353f6" + // `MethodId`
            "6162630000000000000000000000000000000000000000000000000000000000" + // "abc"
            "6465660000000000000000000000000000000000000000000000000000000000"); // "def"

        encoded.Should().BeEquivalentTo(expected);
        var args = AbiCodec.Decode(signature, encoded);
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

        byte[] encoded = AbiCodec.Encode(signature, (69u, true));

        var expected = Bytes.FromHexString(
            "0xcdcd77c0" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000045" + // `69`
            "0000000000000000000000000000000000000000000000000000000000000001"); // `true`
        encoded.Should().BeEquivalentTo(expected);

        (UInt32 a, bool b) = AbiCodec.Decode(signature, encoded);
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

        byte[] encoded = AbiCodec.Encode(signature, ("dave"u8.ToArray(), true, [1, 2, 3]));

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

        (byte[] a, bool b, UInt256[] c) = AbiCodec.Decode(signature, encoded);
        a.Should().BeEquivalentTo("dave"u8.ToArray());
        b.Should().Be(true);
        c.Should().BeEquivalentTo(new UInt256[] { 1, 2, 3 });
    }

    [Test]
    public void DynamicF()
    {
        var signature = new AbiSignature("f")
            .With(
                AbiType.UInt256,
                AbiType.Array(AbiType.UInt32),
                AbiType.BytesM(10),
                AbiType.Bytes);

        var expectedMethodId = Bytes.FromHexString("0x8be65246");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        (UInt256, uint[], byte[], byte[]) arguments = (0x123, [0x456, 0x789], "1234567890"u8.ToArray(), "Hello, world!"u8.ToArray());
        byte[] encoded = AbiCodec.Encode(signature, arguments);

        var expected = Bytes.FromHexString(
            "0x8be65246" +
            "0000000000000000000000000000000000000000000000000000000000000123" +
            "0000000000000000000000000000000000000000000000000000000000000080" +
            "3132333435363738393000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000e0" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000456" +
            "0000000000000000000000000000000000000000000000000000000000000789" +
            "000000000000000000000000000000000000000000000000000000000000000d" +
            "48656c6c6f2c20776f726c642100000000000000000000000000000000000000");
        encoded.Should().BeEquivalentTo(expected);

        var decoded = AbiCodec.Decode(signature, encoded);
        decoded.Should().BeEquivalentTo(arguments);
    }

    [Test]
    public void DynamicG()
    {
        var signature = new AbiSignature("g")
            .With(
                AbiType.Array(AbiType.Array(AbiType.UInt256)),
                AbiType.Array(AbiType.String));

        var expectedMethodId = Bytes.FromHexString("0x2289b18c");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        (UInt256[][], string[]) arguments = ([[1, 2], [3]], ["one", "two", "three"]);
        byte[] encoded = AbiCodec.Encode(signature, arguments);

        var expected = Bytes.FromHexString(
            "0x2289b18c" +
            "0000000000000000000000000000000000000000000000000000000000000040" +
            "0000000000000000000000000000000000000000000000000000000000000140" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000040" +
            "00000000000000000000000000000000000000000000000000000000000000a0" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000001" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000001" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "0000000000000000000000000000000000000000000000000000000000000060" +
            "00000000000000000000000000000000000000000000000000000000000000a0" +
            "00000000000000000000000000000000000000000000000000000000000000e0" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "6f6e650000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "74776f0000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000005" +
            "7468726565000000000000000000000000000000000000000000000000000000");

        encoded.Should().BeEquivalentTo(expected);

        var decoded = AbiCodec.Decode(signature, encoded);
        decoded.Should().BeEquivalentTo(arguments);
    }
}
