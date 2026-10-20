// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Types
{
    private static IEnumerable<TestCaseData> ElementaryTestCases()
    {
        yield return Case(new AbiSignature("abc").With(AbiType.Bool), true,
            "d9aa5f98" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000001"); // `true`

        yield return Case(new AbiSignature("abc").With(AbiType.Address), new Address("0xdcc703c0e500b653ca82273b7bfad8045d85a470"),
            "39ce5e17" + // `MethodId`
            "000000000000000000000000dcc703c0e500b653ca82273b7bfad8045d85a470"); // `0xdcc703c0e500b653ca82273b7bfad8045d85a470`

        yield return Case(new AbiSignature("abc").With(AbiType.BytesM(1)), Bytes.FromHexString("01"),
            "9854c850" + // `MethodId`
            "0100000000000000000000000000000000000000000000000000000000000000"); // Contents of the bytes, right-padded

        yield return Case(new AbiSignature("abc").With(AbiType.BytesM(19)), Bytes.FromHexString("0102030405060708090a0b0c0d0e0f10111213"),
            "e32d0436" + // `MethodId`
            "0102030405060708090a0b0c0d0e0f1011121300000000000000000000000000"); // Contents of the bytes, right-padded

        yield return Case(new AbiSignature("abc").With(AbiType.BytesM(32)), Bytes.FromHexString("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"),
            "cd200edb" + // `MethodId`
            "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20"); // Contents of the bytes

        yield return Case(new AbiSignature("abc").With(AbiType.Bytes), Bytes.FromHexString("0102030405060708090a0b0c0d0e0f1011"),
            "02035fa1" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the bytes
            "0000000000000000000000000000000000000000000000000000000000000011" + // Length of the bytes, `17`
            "0102030405060708090a0b0c0d0e0f1011000000000000000000000000000000"); // Contents of the bytes, right-padded

        yield return Case(new AbiSignature("abc").With(AbiType.String), "é€😀", label: "non-ASCII", encoding:
            "5b84a1ed" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the string
            "0000000000000000000000000000000000000000000000000000000000000009" + // Length of the string in UTF-8 bytes, `9`
            "c3a9e282acf09f98800000000000000000000000000000000000000000000000"); // UTF-8 encoding of `é€😀`, right-padded
    }

    private static IEnumerable<TestCaseData> ArrayTestCases()
    {
        yield return Case(new AbiSignature("abc").With(AbiType.Array(AbiType.UInt256)), [1, 2, 3], label: "length 3", encoding:
            "97a75e1c" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element, `1`
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second element, `2`
            "0000000000000000000000000000000000000000000000000000000000000003"); // Third element, `3`

        yield return Case(new AbiSignature("abc").With(AbiType.Array(AbiType.UInt256, 2)), [1, 1], label: "length 2", encoding:
            "65fd1cd1" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element, `1`
            "0000000000000000000000000000000000000000000000000000000000000001"); // Second element, `1`

        AbiSignature<UInt256[][]> uint256Arrays = new AbiSignature("abc").With(AbiType.Array(AbiType.Array(AbiType.UInt256)));

        yield return Case(uint256Arrays, [[1, 2, 3], [1, 2, 3]], label: "length 2", encoding:
            "e2182e4a" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first inner array
            "00000000000000000000000000000000000000000000000000000000000000c0" + // Offset to second inner array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of first inner array
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element of first inner array, `1`
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second element of first inner array, `2`
            "0000000000000000000000000000000000000000000000000000000000000003" + // Third element of first inner array, `3`
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of second inner array
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element of second inner array, `1`
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second element of second inner array, `2`
            "0000000000000000000000000000000000000000000000000000000000000003"); // Third element of second inner array, `3`

        yield return Case(uint256Arrays, [], label: "length 0", encoding:
            "e2182e4a" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000000"); // Length of the array

        yield return Case(new AbiSignature("abc").With(AbiType.Array(AbiType.Array(AbiType.UInt256, 2), 3)), [[1, 1], [1, 1], [1, 1]], label: "length 3", encoding:
            "9a3f83ca" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element of first fixed array, `1`
            "0000000000000000000000000000000000000000000000000000000000000001" + // Second element of first fixed array, `1`
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element of second fixed array, `1`
            "0000000000000000000000000000000000000000000000000000000000000001" + // Second element of second fixed array, `1`
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element of third fixed array, `1`
            "0000000000000000000000000000000000000000000000000000000000000001"); // Second element of third fixed array, `1`

        AbiSignature<ulong[][]> uint64Triples = new AbiSignature("abc").With(AbiType.Array(AbiType.Array(AbiType.UInt64, 3)));

        yield return Case(uint64Triples, [[100, 200, 300], [400, 500, 600]], label: "length 2", encoding:
            "b59caf90" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000064" + // First element of first fixed array, `100`
            "00000000000000000000000000000000000000000000000000000000000000c8" + // Second element of first fixed array, `200`
            "000000000000000000000000000000000000000000000000000000000000012c" + // Third element of first fixed array, `300`
            "0000000000000000000000000000000000000000000000000000000000000190" + // First element of second fixed array, `400`
            "00000000000000000000000000000000000000000000000000000000000001f4" + // Second element of second fixed array, `500`
            "0000000000000000000000000000000000000000000000000000000000000258"); // Third element of second fixed array, `600`

        yield return Case(uint64Triples, [[1_000_000, 7, 3600]], label: "length 1", encoding:
            "b59caf90" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the array
            "00000000000000000000000000000000000000000000000000000000000f4240" + // First element of the fixed array, `1_000_000`
            "0000000000000000000000000000000000000000000000000000000000000007" + // Second element of the fixed array, `7`
            "0000000000000000000000000000000000000000000000000000000000000e10"); // Third element of the fixed array, `3600`

        yield return Case(uint64Triples, [], label: "length 0", encoding:
            "b59caf90" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000000"); // Length of the array
    }

    private static IEnumerable<TestCaseData> TupleTestCases()
    {
        Address systemUser = new("0xfffffffffffffffffffffffffffffffffffffffe");
        Abi<(UInt256, Address, bool)> staticTuple = AbiType.Tuple(AbiType.UInt256, AbiType.Address, AbiType.Bool);

        yield return Case(new AbiSignature("abc").With(staticTuple), ((UInt256)1000, systemUser, true),
            "3556e1a4" + // `MethodId`
            "00000000000000000000000000000000000000000000000000000000000003e8" + // First component, `1000`
            "000000000000000000000000fffffffffffffffffffffffffffffffffffffffe" + // Second component, `0xfffffffffffffffffffffffffffffffffffffffe`
            "0000000000000000000000000000000000000000000000000000000000000001"); // Third component, `true`

        yield return Case(new AbiSignature("abc").With(AbiType.Tuple(AbiType.Bytes, AbiType.Address, AbiType.Bytes)),
            (Bytes.FromHexString("004749fa3d"), systemUser, new byte[32]),
            "dfdb7bcd" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the tuple
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first component
            "000000000000000000000000fffffffffffffffffffffffffffffffffffffffe" + // Second component, `0xfffffffffffffffffffffffffffffffffffffffe`
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to third component
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first component
            "004749fa3d000000000000000000000000000000000000000000000000000000" + // Contents of first component
            "0000000000000000000000000000000000000000000000000000000000000020" + // Length of third component, `32`
            "0000000000000000000000000000000000000000000000000000000000000000"); // Contents of third component

        yield return Case(new AbiSignature("abc").With(staticTuple, AbiType.String), (((UInt256)1000, systemUser, true), "hello there!"),
            "3cdff546" + // `MethodId`
            "00000000000000000000000000000000000000000000000000000000000003e8" + // First component of first argument, `1000`
            "000000000000000000000000fffffffffffffffffffffffffffffffffffffffe" + // Second component of first argument, `0xfffffffffffffffffffffffffffffffffffffffe`
            "0000000000000000000000000000000000000000000000000000000000000001" + // Third component of first argument, `true`
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second argument
            "000000000000000000000000000000000000000000000000000000000000000c" + // Length of second argument
            "68656c6c6f207468657265210000000000000000000000000000000000000000"); // "hello there!"

        yield return Case(new AbiSignature("abc").With(AbiType.String, staticTuple), ("hello there!", ((UInt256)1000, systemUser, true)),
            "7f15553b" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to first argument
            "00000000000000000000000000000000000000000000000000000000000003e8" + // First component of second argument, `1000`
            "000000000000000000000000fffffffffffffffffffffffffffffffffffffffe" + // Second component of second argument, `0xfffffffffffffffffffffffffffffffffffffffe`
            "0000000000000000000000000000000000000000000000000000000000000001" + // Third component of second argument, `true`
            "000000000000000000000000000000000000000000000000000000000000000c" + // Length of first argument
            "68656c6c6f207468657265210000000000000000000000000000000000000000"); // "hello there!"

        yield return Case(new AbiSignature("abc").With(AbiType.Tuple(AbiType.UInt256, AbiType.Tuple(AbiType.UInt256, AbiType.Address), AbiType.Bool)),
            ((UInt256)1000, ((UInt256)400, systemUser), true),
            "95cb6643" + // `MethodId`
            "00000000000000000000000000000000000000000000000000000000000003e8" + // First component of the outer tuple, `1000`
            "0000000000000000000000000000000000000000000000000000000000000190" + // First component of the inner tuple, `400`
            "000000000000000000000000fffffffffffffffffffffffffffffffffffffffe" + // Second component of the inner tuple, `0xfffffffffffffffffffffffffffffffffffffffe`
            "0000000000000000000000000000000000000000000000000000000000000001"); // Third component of the outer tuple, `true`

        yield return Case(new AbiSignature("abc").With(AbiType.Tuple(AbiType.UInt256, AbiType.Tuple(AbiType.Bytes, AbiType.Address), AbiType.Bool)),
            ((UInt256)1000, (Bytes.FromHexString("019283fa3d"), systemUser), true),
            "0ef81694" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the outer tuple
            "00000000000000000000000000000000000000000000000000000000000003e8" + // First component of the outer tuple, `1000`
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to the inner tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // Third component of the outer tuple, `true`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first component of the inner tuple
            "000000000000000000000000fffffffffffffffffffffffffffffffffffffffe" + // Second component of the inner tuple, `0xfffffffffffffffffffffffffffffffffffffffe`
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first component of the inner tuple
            "019283fa3d000000000000000000000000000000000000000000000000000000"); // Contents of first component of the inner tuple

        yield return Case(new AbiSignature("abc").With(AbiType.Tuple(AbiType.Bytes, AbiType.Tuple(AbiType.Bytes, AbiType.Address), AbiType.Bool)),
            (Bytes.FromHexString("019283fa3d"), (Bytes.FromHexString("019283fa3d"), systemUser), true),
            "20a42dbe" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the outer tuple
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first component of the outer tuple
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to the inner tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // Third component of the outer tuple, `true`
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first component of the outer tuple
            "019283fa3d000000000000000000000000000000000000000000000000000000" + // Contents of first component of the outer tuple
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first component of the inner tuple
            "000000000000000000000000fffffffffffffffffffffffffffffffffffffffe" + // Second component of the inner tuple, `0xfffffffffffffffffffffffffffffffffffffffe`
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first component of the inner tuple
            "019283fa3d000000000000000000000000000000000000000000000000000000"); // Contents of first component of the inner tuple

        yield return Case(new AbiSignature("abc").With(AbiType.Tuple(AbiType.Tuple(AbiType.Tuple(AbiType.UInt256)))), (UInt256)88888,
            "3aa699ea" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000015b38"); // Component of the innermost tuple, `88888`

        yield return Case(new AbiSignature("f").With(AbiType.Tuple()), default,
            "7a94af6f"); // `MethodId`, with nothing after it since `()` encodes to no bytes

        yield return Case(new AbiSignature("f").With(AbiType.Array(AbiType.Tuple(), 2)), new ValueTuple[2],
            "a978e234"); // `MethodId`, with nothing after it since `()[2]` encodes to no bytes
    }

    private static TestCaseData Case<T>(AbiSignature<T> signature, T value, string encoding, string label = null) =>
        new TestCaseData(signature, value, encoding).SetArgDisplayNames(label is null ? [signature.ToString()] : [signature.ToString(), label]);

    [TestCaseSource(nameof(ElementaryTestCases))]
    [TestCaseSource(nameof(ArrayTestCases))]
    [TestCaseSource(nameof(TupleTestCases))]
    public void RoundTrip<T>(AbiSignature<T> signature, T value, string encoding)
    {
        byte[] expected = Bytes.FromHexString(encoding);

        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expected[..AbiSignature.MethodIdLength]));
        Assert.That(AbiCodec.Encode(signature, value), Is.EqualTo(expected));
        Assert.That(AbiCodec.Decode(signature, expected), Is.EqualTo(value));
    }

    [Test]
    public void TupleName()
    {
        Abi<(byte, ulong)> tuple = AbiType.Tuple(AbiType.UInt8, AbiType.UInt64);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tuple.Name, Is.EqualTo("(uint8,uint64)"));
            Assert.That(AbiType.Array(tuple).Name, Is.EqualTo("(uint8,uint64)[]"));
        }
    }
}
