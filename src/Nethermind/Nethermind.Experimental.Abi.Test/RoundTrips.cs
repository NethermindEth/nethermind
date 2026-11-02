// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class RoundTrips
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
        AbiSignature<UInt256[][]> uint256Arrays = new AbiSignature("abc").With(AbiType.Array(AbiType.Array(AbiType.UInt256)));

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

        yield return Case(uint64Triples, [], label: "length 0", encoding:
            "b59caf90" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000000"); // Length of the array
    }

    private static IEnumerable<TestCaseData> TupleTestCases()
    {
        Address systemUser = new("0xfffffffffffffffffffffffffffffffffffffffe");
        Abi<(UInt256, Address, bool)> staticTuple = AbiType.Tuple(AbiType.UInt256, AbiType.Address, AbiType.Bool);

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

    private static IEnumerable<TestCaseData> ArityTestCases()
    {
        Address alice = new("0x5a4eab120fb44eb6684e5e32785702ff45ea344d");
        Address bob = new("0xdac17f958d2ee523a2206206994597c13d831ec7");

        yield return Case(new AbiSignature("allowedTxTypes").With(AbiType.Address, AbiType.Address, AbiType.UInt256, AbiType.UInt256, AbiType.Bytes),
            (alice, bob, (UInt256)1_000_000_000_000_000_000, (UInt256)20_000_000_000, Bytes.FromHexString("a9059cbb")),
            "b9056afa" + // `MethodId`
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // First argument, `sender`
            "000000000000000000000000dac17f958d2ee523a2206206994597c13d831ec7" + // Second argument, `to`
            "0000000000000000000000000000000000000000000000000de0b6b3a7640000" + // Third argument, `value`, `10^18`
            "00000000000000000000000000000000000000000000000000000004a817c800" + // Fourth argument, `gasPrice`, `2 * 10^10`
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to fifth argument, `data`
            "0000000000000000000000000000000000000000000000000000000000000004" + // Length of fifth argument
            "a9059cbb00000000000000000000000000000000000000000000000000000000"); // Contents of fifth argument

        yield return Case(new AbiSignature("DepositEvent").With(AbiType.Bytes, AbiType.Bytes, AbiType.Bytes, AbiType.Bytes, AbiType.Bytes),
            (Repeat(0x11, 48), Repeat(0x22, 32), Repeat(0x33, 8), Repeat(0x44, 96), Repeat(0x55, 4)),
            "649bbc62" + // `MethodId`
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to first argument, `pubkey`
            "0000000000000000000000000000000000000000000000000000000000000100" + // Offset to second argument, `withdrawal_credentials`
            "0000000000000000000000000000000000000000000000000000000000000140" + // Offset to third argument, `amount`
            "0000000000000000000000000000000000000000000000000000000000000180" + // Offset to fourth argument, `signature`
            "0000000000000000000000000000000000000000000000000000000000000200" + // Offset to fifth argument, `index`
            "0000000000000000000000000000000000000000000000000000000000000030" + // Length of first argument, `48`
            "1111111111111111111111111111111111111111111111111111111111111111" + // Contents of first argument
            "1111111111111111111111111111111100000000000000000000000000000000" + // Contents of first argument, continued
            "0000000000000000000000000000000000000000000000000000000000000020" + // Length of second argument, `32`
            "2222222222222222222222222222222222222222222222222222222222222222" + // Contents of second argument
            "0000000000000000000000000000000000000000000000000000000000000008" + // Length of third argument, `8`
            "3333333333333333000000000000000000000000000000000000000000000000" + // Contents of third argument
            "0000000000000000000000000000000000000000000000000000000000000060" + // Length of fourth argument, `96`
            "4444444444444444444444444444444444444444444444444444444444444444" + // Contents of fourth argument
            "4444444444444444444444444444444444444444444444444444444444444444" + // Contents of fourth argument, continued
            "4444444444444444444444444444444444444444444444444444444444444444" + // Contents of fourth argument, continued
            "0000000000000000000000000000000000000000000000000000000000000004" + // Length of fifth argument, `4`
            "5555555500000000000000000000000000000000000000000000000000000000"); // Contents of fifth argument

        yield return Case(new AbiSignature("TransactionSubmitted").With(AbiType.UInt64, AbiType.UInt64, AbiType.Bytes32, AbiType.Address, AbiType.Bytes, AbiType.UInt256),
            (3UL, 42UL, ValueKeccak.Compute("a"), alice, "cipher"u8.ToArray(), (UInt256)21_000),
            "a7f1b546" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000003" + // First argument, `eon`, `3`
            "000000000000000000000000000000000000000000000000000000000000002a" + // Second argument, `txIndex`, `42`
            "3ac225168df54212a25c1c01fd35bebfea408fdac2e31ddd6f80a4bbf9a5f1cb" + // Third argument, `identityPrefix`, `keccak256("a")`
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // Fourth argument, `sender`
            "00000000000000000000000000000000000000000000000000000000000000c0" + // Offset to fifth argument, `encryptedTransaction`
            "0000000000000000000000000000000000000000000000000000000000005208" + // Sixth argument, `gasLimit`, `21_000`
            "0000000000000000000000000000000000000000000000000000000000000006" + // Length of fifth argument
            "6369706865720000000000000000000000000000000000000000000000000000"); // "cipher"

        yield return Case(new AbiSignature("allowedTxTypes").With(AbiType.Address, AbiType.Address, AbiType.UInt256, AbiType.UInt256, AbiType.UInt256, AbiType.UInt256, AbiType.Bytes),
            (bob, alice, (UInt256)1_000_000_000_000_000_000, (UInt256)30_000_000_000, (UInt256)2_000_000_000, (UInt256)21_000, Bytes.FromHexString("d0e30db0")),
            "e4e3b5e5" + // `MethodId`
            "000000000000000000000000dac17f958d2ee523a2206206994597c13d831ec7" + // First argument, `_sender`
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // Second argument, `_to`
            "0000000000000000000000000000000000000000000000000de0b6b3a7640000" + // Third argument, `_value`, `10^18`
            "00000000000000000000000000000000000000000000000000000006fc23ac00" + // Fourth argument, `_maxFeePerGas`, `3 * 10^10`
            "0000000000000000000000000000000000000000000000000000000077359400" + // Fifth argument, `_maxInclusionFeePerGas`, `2 * 10^9`
            "0000000000000000000000000000000000000000000000000000000000005208" + // Sixth argument, `_gasLimit`, `21_000`
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to seventh argument, `_data`
            "0000000000000000000000000000000000000000000000000000000000000004" + // Length of seventh argument
            "d0e30db000000000000000000000000000000000000000000000000000000000"); // Contents of seventh argument

        // Between them, these cases place a dynamic component at every position of the 6- and 7-component tuples
        yield return Case(new AbiSignature("f").With(AbiType.String, AbiType.UInt256, AbiType.Bytes, AbiType.Address, AbiType.String, AbiType.Bytes),
            ("a", (UInt256)2, Bytes.FromHexString("bbbb"), alice, "ccc", Bytes.FromHexString("eeeeeeee")),
            "da46e2f9" + // `MethodId`
            "00000000000000000000000000000000000000000000000000000000000000c0" + // Offset to first argument
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second argument, `2`
            "0000000000000000000000000000000000000000000000000000000000000100" + // Offset to third argument
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // Fourth argument, `0x5a4eab120fb44eb6684e5e32785702ff45ea344d`
            "0000000000000000000000000000000000000000000000000000000000000140" + // Offset to fifth argument
            "0000000000000000000000000000000000000000000000000000000000000180" + // Offset to sixth argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first argument
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of third argument
            "bbbb000000000000000000000000000000000000000000000000000000000000" + // Contents of third argument
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of fifth argument
            "6363630000000000000000000000000000000000000000000000000000000000" + // "ccc"
            "0000000000000000000000000000000000000000000000000000000000000004" + // Length of sixth argument
            "eeeeeeee00000000000000000000000000000000000000000000000000000000"); // Contents of sixth argument

        yield return Case(new AbiSignature("f").With(AbiType.UInt256, AbiType.String, AbiType.Address, AbiType.Bytes, AbiType.Bool, AbiType.String),
            ((UInt256)7, "b", bob, Bytes.FromHexString("dddd"), true, "fff"),
            "37605531" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000007" + // First argument, `7`
            "00000000000000000000000000000000000000000000000000000000000000c0" + // Offset to second argument
            "000000000000000000000000dac17f958d2ee523a2206206994597c13d831ec7" + // Third argument, `0xdac17f958d2ee523a2206206994597c13d831ec7`
            "0000000000000000000000000000000000000000000000000000000000000100" + // Offset to fourth argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Fifth argument, `true`
            "0000000000000000000000000000000000000000000000000000000000000140" + // Offset to sixth argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second argument
            "6200000000000000000000000000000000000000000000000000000000000000" + // "b"
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of fourth argument
            "dddd000000000000000000000000000000000000000000000000000000000000" + // Contents of fourth argument
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of sixth argument
            "6666660000000000000000000000000000000000000000000000000000000000"); // "fff"

        yield return Case(new AbiSignature("f").With(AbiType.String, AbiType.UInt256, AbiType.Bytes, AbiType.Address, AbiType.String, AbiType.Bool, AbiType.Bytes),
            ("a", (UInt256)2, Bytes.FromHexString("bbbb"), alice, "ccc", true, Bytes.FromHexString("eeeeeeee")),
            "c22b95dc" + // `MethodId`
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to first argument
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second argument, `2`
            "0000000000000000000000000000000000000000000000000000000000000120" + // Offset to third argument
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // Fourth argument, `0x5a4eab120fb44eb6684e5e32785702ff45ea344d`
            "0000000000000000000000000000000000000000000000000000000000000160" + // Offset to fifth argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Sixth argument, `true`
            "00000000000000000000000000000000000000000000000000000000000001a0" + // Offset to seventh argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first argument
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of third argument
            "bbbb000000000000000000000000000000000000000000000000000000000000" + // Contents of third argument
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of fifth argument
            "6363630000000000000000000000000000000000000000000000000000000000" + // "ccc"
            "0000000000000000000000000000000000000000000000000000000000000004" + // Length of seventh argument
            "eeeeeeee00000000000000000000000000000000000000000000000000000000"); // Contents of seventh argument

        yield return Case(new AbiSignature("f").With(AbiType.UInt256, AbiType.String, AbiType.Address, AbiType.Bytes, AbiType.Bool, AbiType.String, AbiType.Bytes),
            ((UInt256)7, "b", bob, Bytes.FromHexString("dddd"), true, "fff", Bytes.FromHexString("99999999")),
            "88d33485" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000007" + // First argument, `7`
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to second argument
            "000000000000000000000000dac17f958d2ee523a2206206994597c13d831ec7" + // Third argument, `0xdac17f958d2ee523a2206206994597c13d831ec7`
            "0000000000000000000000000000000000000000000000000000000000000120" + // Offset to fourth argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Fifth argument, `true`
            "0000000000000000000000000000000000000000000000000000000000000160" + // Offset to sixth argument
            "00000000000000000000000000000000000000000000000000000000000001a0" + // Offset to seventh argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second argument
            "6200000000000000000000000000000000000000000000000000000000000000" + // "b"
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of fourth argument
            "dddd000000000000000000000000000000000000000000000000000000000000" + // Contents of fourth argument
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of sixth argument
            "6666660000000000000000000000000000000000000000000000000000000000" + // "fff"
            "0000000000000000000000000000000000000000000000000000000000000004" + // Length of seventh argument
            "9999999900000000000000000000000000000000000000000000000000000000"); // Contents of seventh argument
    }

    private static IEnumerable<TestCaseData> UnnamedTestCases()
    {
        yield return Case(new AbiSignature().With(AbiType.Bytes), [],
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the bytes
            "0000000000000000000000000000000000000000000000000000000000000000"); // Length of the bytes

        yield return Case(new AbiSignature().With(AbiType.Array(AbiType.Tuple(AbiType.Address, AbiType.BytesM(4), AbiType.UInt256))),
            [(new Address("0x5a4eab120fb44eb6684e5e32785702ff45ea344d"), [0xa9, 0x05, 0x9c, 0xbb], 1)],
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the array
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // `target`
            "a9059cbb00000000000000000000000000000000000000000000000000000000" + // `fnSignature`
            "0000000000000000000000000000000000000000000000000000000000000001"); // `value`
    }

    private static IEnumerable<TestCaseData> IntegerTestCases()
    {
        yield return IntegerCase(AbiType.UInt8, byte.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.UInt8, byte.MaxValue,
            "00000000000000000000000000000000000000000000000000000000000000ff"); // `255`

        yield return IntegerCase(AbiType.UInt16, ushort.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.UInt16, ushort.MaxValue,
            "000000000000000000000000000000000000000000000000000000000000ffff"); // `2^16 - 1`

        yield return IntegerCase(AbiType.UInt32, uint.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.UInt32, uint.MaxValue,
            "00000000000000000000000000000000000000000000000000000000ffffffff"); // `2^32 - 1`

        yield return IntegerCase(AbiType.UInt64, ulong.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.UInt64, ulong.MaxValue,
            "000000000000000000000000000000000000000000000000ffffffffffffffff"); // `2^64 - 1`

        yield return IntegerCase(AbiType.UInt256, UInt256.Zero,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.UInt256, UInt256.MaxValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `2^256 - 1`

        yield return IntegerCase(AbiType.Int8, sbyte.MinValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff80"); // `-128`
        yield return IntegerCase(AbiType.Int8, (sbyte)-1,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return IntegerCase(AbiType.Int8, (sbyte)0,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.Int8, sbyte.MaxValue,
            "000000000000000000000000000000000000000000000000000000000000007f"); // `127`

        yield return IntegerCase(AbiType.Int16, short.MinValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8000"); // `-2^15`
        yield return IntegerCase(AbiType.Int16, (short)-1,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return IntegerCase(AbiType.Int16, (short)0,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.Int16, short.MaxValue,
            "0000000000000000000000000000000000000000000000000000000000007fff"); // `2^15 - 1`

        yield return IntegerCase(AbiType.Int32, int.MinValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffff80000000"); // `-2^31`
        yield return IntegerCase(AbiType.Int32, -1,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return IntegerCase(AbiType.Int32, 0,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.Int32, int.MaxValue,
            "000000000000000000000000000000000000000000000000000000007fffffff"); // `2^31 - 1`

        yield return IntegerCase(AbiType.Int64, long.MinValue,
            "ffffffffffffffffffffffffffffffffffffffffffffffff8000000000000000"); // `-2^63`
        yield return IntegerCase(AbiType.Int64, -1L,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return IntegerCase(AbiType.Int64, 0L,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.Int64, long.MaxValue,
            "0000000000000000000000000000000000000000000000007fffffffffffffff"); // `2^63 - 1`

        yield return IntegerCase(AbiType.Int256, new Int256.Int256(-BigInteger.Pow(2, 255)),
            "8000000000000000000000000000000000000000000000000000000000000000"); // `-2^255`
        yield return IntegerCase(AbiType.Int256, Int256.Int256.MinusOne,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return IntegerCase(AbiType.Int256, Int256.Int256.Zero,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(AbiType.Int256, Int256.Int256.Max,
            "7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `2^255 - 1`

        Abi<UInt128> uint128 = AbiType.UIntN<UInt128>();
        yield return IntegerCase(uint128, UInt128.MinValue,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(uint128, UInt128.MaxValue,
            "00000000000000000000000000000000ffffffffffffffffffffffffffffffff"); // `2^128 - 1`

        Abi<Int128> int128 = AbiType.IntN<Int128>();
        yield return IntegerCase(int128, Int128.MinValue,
            "ffffffffffffffffffffffffffffffff80000000000000000000000000000000"); // `-2^127`
        yield return IntegerCase(int128, Int128.NegativeOne,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"); // `-1`
        yield return IntegerCase(int128, Int128.Zero,
            "0000000000000000000000000000000000000000000000000000000000000000"); // `0`
        yield return IntegerCase(int128, Int128.MaxValue,
            "000000000000000000000000000000007fffffffffffffffffffffffffffffff"); // `2^127 - 1`
    }

    private static IEnumerable<TestCaseData> DynamicTestCases()
    {
        yield return Case(new AbiSignature("f").With(AbiType.String, AbiType.String, AbiType.String), ("hello", "my", "friends"),
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

        yield return Case(new AbiSignature("f").With(AbiType.Array(AbiType.Bool)), [true, false, true],
            "da095a04" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element, `true`
            "0000000000000000000000000000000000000000000000000000000000000000" + // Second element, `false`
            "0000000000000000000000000000000000000000000000000000000000000001"); // Third element, `true`

        yield return Case(new AbiSignature("f").With(AbiType.UInt256, AbiType.Tuple(AbiType.UInt256, AbiType.String), AbiType.String),
            ((UInt256)2, ((UInt256)1, "a"), "b"),
            "004b756e" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000002" + // First argument, `2`
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to the tuple
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to third argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component of the tuple, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component of the tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second component of the tuple
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of third argument
            "6200000000000000000000000000000000000000000000000000000000000000"); // "b"

        yield return Case(new AbiSignature("f").With(AbiType.Array(AbiType.Tuple(AbiType.String, AbiType.UInt256, AbiType.String))), [("a", 1, "b"), ("c", 2, "d")],
            "689595a5" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element
            "0000000000000000000000000000000000000000000000000000000000000120" + // Offset to second element
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first component of first element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Second component of first element, `1`
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to third component of first element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first component of first element
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of third component of first element
            "6200000000000000000000000000000000000000000000000000000000000000" + // "b"
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first component of second element
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second component of second element, `2`
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to third component of second element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first component of second element
            "6300000000000000000000000000000000000000000000000000000000000000" + // "c"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of third component of second element
            "6400000000000000000000000000000000000000000000000000000000000000"); // "d"

        yield return Case(new AbiSignature("f").With(AbiType.Array(AbiType.String, 2)), ["a", "b"],
            "d72d3724" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the fixed array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element, relative to the fixed array
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second element, relative to the fixed array
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first element
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second element
            "6200000000000000000000000000000000000000000000000000000000000000"); // "b"

        yield return Case(new AbiSignature("f").With(AbiType.Array(AbiType.Array(AbiType.String, 2))), [["a", "b"], ["c", "d"]],
            "10211b6b" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element
            "0000000000000000000000000000000000000000000000000000000000000100" + // Offset to second element
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string of first element
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string of first element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first string of first element
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second string of first element
            "6200000000000000000000000000000000000000000000000000000000000000" + // "b"
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string of second element
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string of second element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first string of second element
            "6300000000000000000000000000000000000000000000000000000000000000" + // "c"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second string of second element
            "6400000000000000000000000000000000000000000000000000000000000000"); // "d"

        yield return Case(new AbiSignature("f").With(AbiType.Tuple(AbiType.UInt256, AbiType.Array(AbiType.String, 2))), (1, ["a", "b"]),
            "bfb8b830" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component of the tuple, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component of the tuple
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string of second component
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string of second component
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first string of second component
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second string of second component
            "6200000000000000000000000000000000000000000000000000000000000000"); // "b"

        Abi<string> wideString = new()
        {
            Name = AbiType.String.Name,
            Read = AbiType.String.Read,
            Write = AbiType.String.Write,
            HeadSize = 64,
            TailSize = AbiType.String.TailSize
        };
        yield return Case(new AbiSignature("f").With(AbiType.UInt256, wideString), ((UInt256)1, "a"), label: "string declaring head size 64", encoding:
            "1ba84e08" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000001" + // First argument, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second argument
            "6100000000000000000000000000000000000000000000000000000000000000"); // "a"
    }

    /// <remarks>
    /// The tests have been provided by the ABI specification and by well-known contracts such as ERC-20.
    /// See: https://docs.soliditylang.org/en/latest/abi-spec.html#formal-specification-of-the-encoding
    /// </remarks>
    private static IEnumerable<TestCaseData> ExampleTestCases()
    {
        yield return Case(new AbiSignature("bar").With(AbiType.Array(AbiType.BytesM(3), length: 2)), [[(byte)'a', (byte)'b', (byte)'c'], [(byte)'d', (byte)'e', (byte)'f']],
            "0xfce353f6" + // `MethodId`
            "6162630000000000000000000000000000000000000000000000000000000000" + // "abc"
            "6465660000000000000000000000000000000000000000000000000000000000"); // "def"

        yield return Case(new AbiSignature("baz").With(AbiType.UInt32, AbiType.Bool), (69u, true),
            "0xcdcd77c0" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000045" + // `69`
            "0000000000000000000000000000000000000000000000000000000000000001"); // `true`

        yield return Case(new AbiSignature("sam").With(AbiType.Bytes, AbiType.Bool, AbiType.Array(AbiType.UInt)), ("dave"u8.ToArray(), true, [1, 2, 3]),
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

        yield return Case(new AbiSignature("f").With(AbiType.UInt256, AbiType.Array(AbiType.UInt32), AbiType.BytesM(10), AbiType.Bytes),
            (0x123, [0x456, 0x789], "1234567890"u8.ToArray(), "Hello, world!"u8.ToArray()),
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

        yield return Case(new AbiSignature("g").With(AbiType.Array(AbiType.Array(AbiType.UInt256)), AbiType.Array(AbiType.String)),
            ([[1, 2], [3]], ["one", "two", "three"]),
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

        yield return Case(new AbiSignature("transfer").With(AbiType.Address, AbiType.UInt256),
            (new Address("0x5a4eab120fb44eb6684e5e32785702ff45ea344d"), (UInt256)1_000_000_000_000_000_000),
            "0xa9059cbb" + // `MethodId`
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d" + // `0x5a4eab120fb44eb6684e5e32785702ff45ea344d`
            "0000000000000000000000000000000000000000000000000de0b6b3a7640000"); // `1_000_000_000_000_000_000`

        yield return Case(new AbiSignature("commitHash").With(AbiType.Bytes32, AbiType.Bytes), (ValueKeccak.Compute("secret"), "cipher"u8.ToArray()),
            "0x0b61ba85" + // `MethodId`
            "65462b0520ef7d3df61b9992ed3bea0c56ead753be7c8b3614e0ce01e4cac41b" + // First argument, `keccak256("secret")`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Location of the data part of the second parameter
            "0000000000000000000000000000000000000000000000000000000000000006" + // Data part of the second argument
            "6369706865720000000000000000000000000000000000000000000000000000"); // Contents of the second argument, `"cipher"`
    }

    private static TestCaseData Case<T>(AbiSignature<T> signature, T value, string encoding, string label = null) =>
        new TestCaseData(signature, value, encoding).SetArgDisplayNames(label is null ? [signature.ToString()] : [signature.ToString(), label]);

    private static TestCaseData IntegerCase<T>(Abi<T> abi, T value, string word)
    {
        AbiSignature<T> signature = new AbiSignature("f").With(abi);
        return Case(signature, value, Convert.ToHexStringLower(signature.MethodId) + word, value.ToString());
    }

    private static byte[] Repeat(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    [TestCaseSource(nameof(ElementaryTestCases))]
    [TestCaseSource(nameof(ArrayTestCases))]
    [TestCaseSource(nameof(TupleTestCases))]
    [TestCaseSource(nameof(ArityTestCases))]
    [TestCaseSource(nameof(UnnamedTestCases))]
    [TestCaseSource(nameof(IntegerTestCases))]
    [TestCaseSource(nameof(DynamicTestCases))]
    [TestCaseSource(nameof(ExampleTestCases))]
    public void RoundTrip<T>(AbiSignature<T> signature, T value, string encoding)
    {
        byte[] expected = Bytes.FromHexString(encoding);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expected[..signature.MethodId.Length]));
            Assert.That(AbiCodec.Encode(signature, value), Is.EqualTo(expected));
            Assert.That(AbiCodec.Decode(signature, expected), Is.EqualTo(value));
        }
    }
}
