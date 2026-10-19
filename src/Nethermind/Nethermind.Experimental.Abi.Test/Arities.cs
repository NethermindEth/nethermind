// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Arities
{
    [Test]
    public void ZeroArguments()
    {
        AbiSignature<ValueTuple> signature = new AbiSignature("getCoreState").With();
        byte[] methodId = Bytes.FromHexString("6aa6a01a");
        byte[] otherMethodId = Bytes.FromHexString("c3f909d4"); // `getConfig()`

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbiType.Tuple().Name, Is.EqualTo("()"));
            Assert.That(signature.MethodId.ToArray(), Is.EqualTo(methodId));
            Assert.That(AbiCodec.Encode(signature, default), Is.EqualTo(methodId));
            Assert.DoesNotThrow(() => AbiCodec.Decode(signature, methodId));
            Assert.Throws<AbiException>(() => AbiCodec.Decode(signature, Bytes.Concat(methodId, new byte[32])));
            Assert.Throws<AbiException>(() => AbiCodec.Decode(signature, otherMethodId));
        }
    }

    private static IEnumerable<TestCaseData> RoundTripTestCases()
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

    private static TestCaseData Case<T>(AbiSignature<T> signature, T value, string encoding) =>
        new TestCaseData(signature, value, encoding).SetArgDisplayNames(signature.ToString());

    private static byte[] Repeat(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    [TestCaseSource(nameof(RoundTripTestCases))]
    public void RoundTrip<T>(AbiSignature<T> signature, T value, string encoding)
    {
        byte[] expected = Bytes.FromHexString(encoding);

        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expected[..AbiSignature.MethodIdLength]));
        Assert.That(AbiCodec.Encode(signature, value), Is.EqualTo(expected));
        Assert.That(AbiCodec.Decode(signature, expected), Is.EqualTo(value));
    }
}
