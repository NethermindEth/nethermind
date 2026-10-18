// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

/// <remarks>
/// The tests have been provided by the ABI specification and by well-known contracts such as ERC-20.
/// See: https://docs.soliditylang.org/en/latest/abi-spec.html#formal-specification-of-the-encoding
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class Examples
{
    [Test]
    public void Bar()
    {
        AbiSignature<byte[][]> signature = new AbiSignature("bar")
            .With(
                AbiType.Array(AbiType.BytesM(3), length: 2));

        byte[] expectedMethodId = Bytes.FromHexString("0xfce353f6");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, [[(byte)'a', (byte)'b', (byte)'c'], [(byte)'d', (byte)'e', (byte)'f']]);

        byte[] expected = Bytes.FromHexString(
            "0xfce353f6" + // `MethodId`
            "6162630000000000000000000000000000000000000000000000000000000000" + // "abc"
            "6465660000000000000000000000000000000000000000000000000000000000"); // "def"

        Assert.That(encoded, Is.EqualTo(expected));
        byte[][] args = AbiCodec.Decode(signature, encoded);
        Assert.That(args, Is.EqualTo(new byte[][] { [(byte)'a', (byte)'b', (byte)'c'], [(byte)'d', (byte)'e', (byte)'f'] }));
    }

    [Test]
    public void Baz()
    {
        AbiSignature<(uint, bool)> signature = new AbiSignature("baz")
            .With(
                AbiType.UInt32,
                AbiType.Bool);

        byte[] expectedMethodId = Bytes.FromHexString("0xcdcd77c0");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, (69u, true));

        byte[] expected = Bytes.FromHexString(
            "0xcdcd77c0" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000045" + // `69`
            "0000000000000000000000000000000000000000000000000000000000000001"); // `true`
        Assert.That(encoded, Is.EqualTo(expected));

        (uint, bool) decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo((69u, true)));
    }

    [Test]
    public void Sam()
    {
        AbiSignature<(byte[], bool, UInt256[])> signature = new AbiSignature("sam")
            .With(
                AbiType.Bytes,
                AbiType.Bool,
                AbiType.Array(AbiType.UInt));

        byte[] expectedMethodId = Bytes.FromHexString("0xa5643bf2");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, ("dave"u8.ToArray(), true, [1, 2, 3]));

        byte[] expected = Bytes.FromHexString(
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
        Assert.That(encoded, Is.EqualTo(expected));

        (byte[], bool, UInt256[]) decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(("dave"u8.ToArray(), true, new UInt256[] { 1, 2, 3 })));
    }

    [Test]
    public void DynamicF()
    {
        AbiSignature<(UInt256, uint[], byte[], byte[])> signature = new AbiSignature("f")
            .With(
                AbiType.UInt256,
                AbiType.Array(AbiType.UInt32),
                AbiType.BytesM(10),
                AbiType.Bytes);

        byte[] expectedMethodId = Bytes.FromHexString("0x8be65246");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        (UInt256, uint[], byte[], byte[]) arguments = (0x123, [0x456, 0x789], "1234567890"u8.ToArray(), "Hello, world!"u8.ToArray());
        byte[] encoded = AbiCodec.Encode(signature, arguments);

        byte[] expected = Bytes.FromHexString(
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
        Assert.That(encoded, Is.EqualTo(expected));

        Assert.That(AbiCodec.Decode(signature, encoded), Is.EqualTo(arguments));
    }

    [Test]
    public void DynamicG()
    {
        AbiSignature<(UInt256[][], string[])> signature = new AbiSignature("g")
            .With(
                AbiType.Array(AbiType.Array(AbiType.UInt256)),
                AbiType.Array(AbiType.String));

        byte[] expectedMethodId = Bytes.FromHexString("0x2289b18c");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        (UInt256[][], string[]) arguments = ([[1, 2], [3]], ["one", "two", "three"]);
        byte[] encoded = AbiCodec.Encode(signature, arguments);

        byte[] expected = Bytes.FromHexString(
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

        Assert.That(encoded, Is.EqualTo(expected));

        Assert.That(AbiCodec.Decode(signature, encoded), Is.EqualTo(arguments));
    }

    [Test]
    public void Transfer()
    {
        AbiSignature<(Address, UInt256)> signature = new AbiSignature("transfer")
            .With(
                AbiType.Address,
                AbiType.UInt256);

        byte[] expectedMethodId = Bytes.FromHexString("0xa9059cbb");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        (Address, UInt256) arguments = (new Address("0x5a4eab120fb44eb6684e5e32785702ff45ea344d"), 1_000_000_000_000_000_000);
        byte[] encoded = AbiCodec.Encode(signature, arguments);

        byte[] expected = Bytes.FromHexString(
            "0xa9059cbb" + // `MethodId`
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // `0x5a4eab120fb44eb6684e5e32785702ff45ea344d`
            "0000000000000000000000000000000000000000000000000de0b6b3a7640000"); // `1_000_000_000_000_000_000`
        Assert.That(encoded, Is.EqualTo(expected));

        Assert.That(AbiCodec.Decode(signature, encoded), Is.EqualTo(arguments));
    }

    [Test]
    public void CommitHash()
    {
        AbiSignature<(ValueHash256, byte[])> signature = new AbiSignature("commitHash")
            .With(
                AbiType.Bytes32,
                AbiType.Bytes);

        byte[] expectedMethodId = Bytes.FromHexString("0x0b61ba85");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        (ValueHash256, byte[]) arguments = (ValueKeccak.Compute("secret"), "cipher"u8.ToArray());
        byte[] encoded = AbiCodec.Encode(signature, arguments);

        byte[] expected = Bytes.FromHexString(
            "0x0b61ba85" + // `MethodId`
            "65462b0520ef7d3df61b9992ed3bea0c56ead753be7c8b3614e0ce01e4cac41b" + // First argument, `keccak256("secret")`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Location of the data part of the second parameter
            "0000000000000000000000000000000000000000000000000000000000000006" + // Data part of the second argument
            "6369706865720000000000000000000000000000000000000000000000000000"); // Contents of the second argument, `"cipher"`
        Assert.That(encoded, Is.EqualTo(expected));

        Assert.That(AbiCodec.Decode(signature, encoded), Is.EqualTo(arguments));
    }

    [Test]
    public void CommitHashFromHash256()
    {
        AbiSignature<(ValueHash256, byte[])> signature = new AbiSignature("commitHash")
            .With(
                AbiType.Bytes32,
                AbiType.Bytes);

        Hash256 secretHash = Keccak.Compute("secret");
        byte[] cipher = "cipher"u8.ToArray();
        byte[] encoded = AbiCodec.Encode(signature, (secretHash, cipher));
        Assert.That(encoded, Is.EqualTo(AbiCodec.Encode(signature, (secretHash.ValueHash256, cipher))));

        (ValueHash256 decodedHash, _) = AbiCodec.Decode(signature, encoded);
        Assert.That(decodedHash.ToHash256(), Is.EqualTo(secretHash));
    }
}
