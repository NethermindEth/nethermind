// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Unnamed
{
    private static IEnumerable<TestCaseData> RoundTripTestCases()
    {
        yield return Case(new AbiSignature().With(AbiType.Bytes), [],
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the bytes
            "0000000000000000000000000000000000000000000000000000000000000000"); // Length of the bytes

        yield return Case(new AbiSignature().With(AbiType.UInt64, AbiType.UInt64, AbiType.Bytes32, AbiType.Address, AbiType.Bytes, AbiType.UInt256),
            (3UL, 42UL, ValueKeccak.Compute("a"), new Address("0x5a4eab120fb44eb6684e5e32785702ff45ea344d"), "cipher"u8.ToArray(), (UInt256)21_000),
            "0000000000000000000000000000000000000000000000000000000000000003" + // `eon`, `3`
            "000000000000000000000000000000000000000000000000000000000000002a" + // `txIndex`, `42`
            "3ac225168df54212a25c1c01fd35bebfea408fdac2e31ddd6f80a4bbf9a5f1cb" + // `identityPrefix`, `keccak256("a")`
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // `sender`
            "00000000000000000000000000000000000000000000000000000000000000c0" + // Offset to `encryptedTransaction`
            "0000000000000000000000000000000000000000000000000000000000005208" + // `gasLimit`, `21_000`
            "0000000000000000000000000000000000000000000000000000000000000006" + // Length of `encryptedTransaction`
            "6369706865720000000000000000000000000000000000000000000000000000"); // "cipher"

        yield return Case(new AbiSignature().With(AbiType.Tuple(AbiType.Bytes, AbiType.Bytes)), ([0x11], [0x22, 0x22]),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the struct
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to the first bytes
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to the second bytes
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the first bytes
            "1100000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the second bytes
            "2222000000000000000000000000000000000000000000000000000000000000");

        yield return Case(new AbiSignature().With(AbiType.Array(AbiType.Tuple(AbiType.Address, AbiType.BytesM(4), AbiType.UInt256))),
            [(new Address("0x5a4eab120fb44eb6684e5e32785702ff45ea344d"), [0xa9, 0x05, 0x9c, 0xbb], 1)],
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the array
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // `target`
            "a9059cbb00000000000000000000000000000000000000000000000000000000" + // `fnSignature`
            "0000000000000000000000000000000000000000000000000000000000000001"); // `value`

        yield return Case(new AbiSignature().With(AbiType.Address), new Address("0xdcc703c0e500b653ca82273b7bfad8045d85a470"),
            "000000000000000000000000dcc703c0e500b653ca82273b7bfad8045d85a470"); // `0xdcc703c0e500b653ca82273b7bfad8045d85a470`

        yield return Case(new AbiSignature().With(AbiType.UInt256), (UInt256)98127491,
            "0000000000000000000000000000000000000000000000000000000005d94e83"); // `98127491`

        yield return Case(new AbiSignature().With(AbiType.UInt256, AbiType.Address), ((UInt256)324124, new Address("0xcd2a3d9f938e13cd947ec05abc7fe734df8dd826")),
            "000000000000000000000000000000000000000000000000000000000004f21c" + // First argument, `324124`
            "000000000000000000000000cd2a3d9f938e13cd947ec05abc7fe734df8dd826"); // Second argument, `0xcd2a3d9f938e13cd947ec05abc7fe734df8dd826`

        yield return Case(new AbiSignature().With(), default, "");
    }

    private static TestCaseData Case<T>(AbiSignature<T> signature, T value, string encoding) =>
        new TestCaseData(signature, value, encoding).SetArgDisplayNames(signature.ToString());

    [TestCaseSource(nameof(RoundTripTestCases))]
    public void RoundTrip<T>(AbiSignature<T> signature, T value, string encoding)
    {
        byte[] expected = Bytes.FromHexString(encoding);

        Assert.That(AbiCodec.Encode(signature, value), Is.EqualTo(expected));
        Assert.That(AbiCodec.Decode(signature, expected), Is.EqualTo(value));
    }

    [Test]
    public void NestedPayload()
    {
        Address batcher = new("0x5a4eab120fb44eb6684e5e32785702ff45ea344d");
        byte[] data = Bytes.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to `data`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Length of `data`
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d"); // Contents of `data`, `abi.encode(batcher)`

        byte[] inner = AbiCodec.Decode(new AbiSignature().With(AbiType.Bytes), data);

        Assert.That(inner, Is.EqualTo(data[64..]));
        Assert.That(AbiCodec.Decode(new AbiSignature().With(AbiType.Address), inner), Is.EqualTo(batcher));
    }

    private static IEnumerable<TestCaseData> NamedTestCases()
    {
        yield return Named(static s => s.With(AbiType.UInt32, AbiType.Bool), (69u, true));
        yield return Named(static s => s.With(AbiType.String), "hello");
        yield return Named(static s => s.With(AbiType.Bytes, AbiType.Bool, AbiType.Array(AbiType.UInt)), ("dave"u8.ToArray(), true, [1, 2, 3]));
        yield return Named(static s => s.With(), default);
    }

    private static TestCaseData Named<T>(Func<AbiSignature, AbiSignature<T>> with, T value)
    {
        AbiSignature<T> unnamed = with(new AbiSignature());
        return new TestCaseData(unnamed, with(new AbiSignature("f")), value).SetArgDisplayNames(unnamed.ToString());
    }

    [TestCaseSource(nameof(NamedTestCases))]
    public void MatchesNamedWithoutMethodId<T>(AbiSignature<T> unnamed, AbiSignature<T> named, T value)
    {
        byte[] encoded = AbiCodec.Encode(unnamed, value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unnamed.MethodId.ToArray(), Is.Empty);
            Assert.That(encoded, Is.EqualTo(AbiCodec.Encode(named, value)[AbiSignature.MethodIdLength..]));
            Assert.That(unnamed.Size(value), Is.EqualTo(named.Size(value) - AbiSignature.MethodIdLength));
            Assert.That(AbiCodec.Decode(unnamed, encoded), Is.EqualTo(value));
        }
    }

    [Test]
    public void EmptyName_HasMethodId() =>
        Assert.That(new AbiSignature("").With().MethodId.ToArray(), Is.EqualTo(Bytes.FromHexString("861731d5"))); // `keccak256("()")`
}
