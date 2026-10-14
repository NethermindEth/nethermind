// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class UserErrors
{
    private static IEnumerable<TestCaseData> BytesMTestCases()
    {
        yield return new TestCaseData(4, new byte[] { 1, 2, 3, 4, 5 }).SetName("expected 4, got 5");
        yield return new TestCaseData(3, new byte[] { 1 }).SetName("expected 3, got 1");
        yield return new TestCaseData(2, Array.Empty<byte>()).SetName("expected 2, got 0");
        yield return new TestCaseData(6, new byte[] { 1, 2, 3, 4, 5 }).SetName("expected 6, got 5");
        yield return new TestCaseData(2, new byte[] { 1, 2, 3, 4 }).SetName("expected 2, got 4");
    }

    [TestCaseSource(nameof(BytesMTestCases))]
    public void BytesM_WrongLength(int length, byte[] bytes)
    {
        AbiSignature<byte[]> signature = new AbiSignature("f")
            .With(
                AbiType.BytesM(length)
            );

        Assert.Throws<AbiException>(() => AbiCodec.Encode(signature, bytes));
    }

    private static IEnumerable<TestCaseData> ArrayKTestCases()
    {
        yield return new TestCaseData(3, new[] { 1u, 2u }).SetName("expected 3, got 2");
        yield return new TestCaseData(2, new[] { 1u, 2u, 3u }).SetName("expected 2, got 3");
        yield return new TestCaseData(4, Array.Empty<uint>()).SetName("expected 4, got 0");
        yield return new TestCaseData(1, new[] { 42u, 43u }).SetName("expected 1, got 2");
        yield return new TestCaseData(0, new[] { 99u }).SetName("expected 0, got 1");
    }

    [TestCaseSource(nameof(ArrayKTestCases))]
    public void ArrayK_EncodeWrongLength(int length, UInt32[] elements)
    {
        AbiSignature<uint[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.UInt32, length)
            );

        Assert.Throws<AbiException>(() => AbiCodec.Encode(signature, elements));
    }

    [Test]
    public void Mismatched_MethodId()
    {
        AbiSignature<uint> signature = new AbiSignature("f")
            .With(
                AbiType.UInt32
            );

        byte[] encoded = AbiCodec.Encode(signature, 69u);

        // Change the method id to something else
        encoded[0] ^= 1;

        Assert.Throws<AbiException>(() => AbiCodec.Decode(signature, encoded));
    }
}
