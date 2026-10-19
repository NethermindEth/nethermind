// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Integers
{
    private static IEnumerable<TestCaseData> RoundTripTestCases()
    {
        yield return Case(AbiType.UInt8, "uint8", byte.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.UInt8, "uint8", byte.MaxValue,
            "00000000000000000000000000000000000000000000000000000000000000ff"); // `255`

        yield return Case(AbiType.UInt16, "uint16", ushort.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.UInt16, "uint16", ushort.MaxValue,
            "000000000000000000000000000000000000000000000000000000000000ffff"); // `2^16 - 1`

        yield return Case(AbiType.UInt32, "uint32", uint.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.UInt32, "uint32", uint.MaxValue,
            "00000000000000000000000000000000000000000000000000000000ffffffff"); // `2^32 - 1`

        yield return Case(AbiType.UInt64, "uint64", ulong.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.UInt64, "uint64", ulong.MaxValue,
            "000000000000000000000000000000000000000000000000ffffffffffffffff"); // `2^64 - 1`

        yield return Case(AbiType.UInt256, "uint256", UInt256.Zero,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.UInt256, "uint256", UInt256.MaxValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `2^256 - 1`

        yield return Case(AbiType.Int8, "int8", sbyte.MinValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff80"); // `-128`
        yield return Case(AbiType.Int8, "int8", (sbyte)-1,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return Case(AbiType.Int8, "int8", (sbyte)0,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.Int8, "int8", sbyte.MaxValue,
            "000000000000000000000000000000000000000000000000000000000000007f"); // `127`

        yield return Case(AbiType.Int16, "int16", short.MinValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8000"); // `-2^15`
        yield return Case(AbiType.Int16, "int16", (short)-1,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return Case(AbiType.Int16, "int16", (short)0,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.Int16, "int16", short.MaxValue,
            "0000000000000000000000000000000000000000000000000000000000007fff"); // `2^15 - 1`

        yield return Case(AbiType.Int32, "int32", int.MinValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffff80000000"); // `-2^31`
        yield return Case(AbiType.Int32, "int32", -1,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return Case(AbiType.Int32, "int32", 0,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.Int32, "int32", int.MaxValue,
            "000000000000000000000000000000000000000000000000000000007fffffff"); // `2^31 - 1`

        yield return Case(AbiType.Int64, "int64", long.MinValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffff8000000000000000"); // `-2^63`
        yield return Case(AbiType.Int64, "int64", -1L,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return Case(AbiType.Int64, "int64", 0L,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.Int64, "int64", long.MaxValue,
            "0000000000000000000000000000000000000000000000007fffffffffffffff"); // `2^63 - 1`

        yield return Case(AbiType.Int256, "int256", new Int256.Int256(-BigInteger.Pow(2, 255)),
            "8000000000000000000000000000000000000000000000000000000000000000"); // `-2^255`
        yield return Case(AbiType.Int256, "int256", Int256.Int256.MinusOne,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return Case(AbiType.Int256, "int256", Int256.Int256.Zero,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(AbiType.Int256, "int256", Int256.Int256.Max,
            "7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `2^255 - 1`

        Abi<UInt128> uint128 = AbiType.UIntN<UInt128>();
        yield return Case(uint128, "uint128", UInt128.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(uint128, "uint128", UInt128.MaxValue,
            "00000000000000000000000000000000ffffffffffffffffffffffffffffffff"); // `2^128 - 1`

        Abi<Int128> int128 = AbiType.IntN<Int128>();
        yield return Case(int128, "int128", Int128.MinValue,
            "ffffffffffffffffffffffffffffffff80000000000000000000000000000000"); // `-2^127`
        yield return Case(int128, "int128", Int128.NegativeOne,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return Case(int128, "int128", Int128.Zero,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return Case(int128, "int128", Int128.MaxValue,
            "000000000000000000000000000000007fffffffffffffffffffffffffffffff"); // `2^127 - 1`
    }

    private static TestCaseData Case<T>(Abi<T> abi, string name, T value, string word) =>
        new TestCaseData(abi, name, value, word).SetName($"{name} {value}");

    [TestCaseSource(nameof(RoundTripTestCases))]
    public void RoundTrip<T>(Abi<T> abi, string name, T value, string word)
    {
        AbiSignature<T> signature = new AbiSignature("f").With(abi);
        Assert.That(abi.Name, Is.EqualTo(name));

        byte[] encoded = AbiCodec.Encode(signature, value);
        Assert.That(encoded, Is.EqualTo(Bytes.Concat(signature.MethodId, Bytes.FromHexString(word))));

        Assert.That(AbiCodec.Decode(signature, encoded), Is.EqualTo(value));
    }

    private static IEnumerable<TestCaseData> MethodIdTestCases()
    {
        yield return new TestCaseData(AbiType.Int8, "0a9a2963").SetName("f(int8)");
        yield return new TestCaseData(AbiType.Int, "1c008df9").SetName("f(int256)");
    }

    [TestCaseSource(nameof(MethodIdTestCases))]
    public void MethodId<T>(Abi<T> abi, string expectedMethodId) =>
        Assert.That(new AbiSignature("f").With(abi).MethodId.ToArray(), Is.EqualTo(Bytes.FromHexString(expectedMethodId)));
}
