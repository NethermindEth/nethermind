// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class DirtyBuffer
{
    private static IEnumerable<TestCaseData> WriteTestCases()
    {
        yield return Case(new AbiSignature("f").With(AbiType.String), "hello");
        yield return Case(new AbiSignature("f").With(AbiType.Array(AbiType.Bool)), [true, false, true]);
        yield return Case(new AbiSignature("f").With(AbiType.Array(AbiType.String)), ["hello", "my", "friends"]);
        yield return Case(new AbiSignature("f").With(AbiType.Tuple(AbiType.UInt256, AbiType.String)), ((UInt256)1, "a"));
        yield return Case(new AbiSignature("f").With(AbiType.BytesM(10)), "1234567890"u8.ToArray());
        yield return Case(new AbiSignature("f").With(AbiType.Address), new Address("0x5a4eab120fb44eb6684e5e32785702ff45ea344d"));
        yield return Case(new AbiSignature("f").With(AbiType.Bytes32), ValueKeccak.Compute("a"));
        yield return Case(new AbiSignature("f").With(AbiType.Int8, AbiType.Int64, AbiType.Int256), ((sbyte)-1, 1L, Int256.Int256.One));
        yield return Case(new AbiSignature("sam").With(AbiType.Bytes, AbiType.Bool, AbiType.Array(AbiType.UInt)),
            ("dave"u8.ToArray(), true, [1, 2, 3]));
        yield return Case(new AbiSignature("f").With(AbiType.UInt256, AbiType.Array(AbiType.UInt32), AbiType.BytesM(10), AbiType.Bytes),
            (0x123, [0x456, 0x789], "1234567890"u8.ToArray(), "Hello, world!"u8.ToArray()));
    }

    private static TestCaseData Case<T>(AbiSignature<T> signature, T value) =>
        new TestCaseData(signature, value).SetArgDisplayNames(signature.ToString());

    [TestCaseSource(nameof(WriteTestCases))]
    public void Write_OverwritesStaleBytes<T>(AbiSignature<T> signature, T value)
    {
        int size = signature.Abi.Size(value);
        byte[] buffer = new byte[size];
        Array.Fill(buffer, (byte)0xFF);

        BinarySpanWriter w = new(buffer);
        signature.Abi.Write(ref w, value);

        byte[] expected = AbiCodec.Encode(signature, value)[AbiSignature.MethodIdLength..];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(w.Written, Is.EqualTo(size));
            Assert.That(buffer, Is.EqualTo(expected));
        }
    }
}
