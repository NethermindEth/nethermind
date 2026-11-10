// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Nethermind.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Buffers
{
    private static IEnumerable<TestCaseData> EncodeTestCases()
    {
        yield return Case(new AbiSignature("baz").With(AbiType.UInt32, AbiType.Bool), (69u, true));
        yield return Case(new AbiSignature("f").With(AbiType.String), "hello");
        yield return Case(new AbiSignature("f").With(AbiType.Array(AbiType.String)), ["hello", "my", "friends"]);
        yield return Case(new AbiSignature("sam").With(AbiType.Bytes, AbiType.Bool, AbiType.Array(AbiType.UInt)),
            ("dave"u8.ToArray(), true, [1, 2, 3]));
    }

    private static TestCaseData Case<T>(AbiSignature<T> signature, T value) =>
        new TestCaseData(signature, value).SetArgDisplayNames(signature.ToString());

    [TestCaseSource(nameof(EncodeTestCases))]
    public void Encode_ExactDestination_MatchesArrayEncoding<T>(AbiSignature<T> signature, T value)
    {
        int size = signature.Size(value);
        byte[] destination = new byte[size];

        int written = AbiCodec.Encode(signature, value, destination);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(size));
            Assert.That(destination, Is.EqualTo(AbiCodec.Encode(signature, value)));
        }
    }

    [TestCaseSource(nameof(EncodeTestCases))]
    public void Encode_LargerDestination_LeavesExcessUntouched<T>(AbiSignature<T> signature, T value)
    {
        const byte sentinel = 0xAA;
        int size = signature.Size(value);
        byte[] destination = new byte[size + 32];
        Array.Fill(destination, sentinel);

        int written = AbiCodec.Encode(signature, value, destination);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(size));
            Assert.That(destination[..size], Is.EqualTo(AbiCodec.Encode(signature, value)));
            Assert.That(destination[size..], Is.All.EqualTo(sentinel));
        }
    }

    [TestCaseSource(nameof(EncodeTestCases))]
    public void Encode_DestinationTooSmall_Throws<T>(AbiSignature<T> signature, T value)
    {
        byte[] destination = new byte[signature.Size(value) - 1];

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<AbiException>(() => AbiCodec.Encode(signature, value, destination));
            Assert.Throws<AbiException>(() => AbiCodec.Encode(signature, value, Span<byte>.Empty));
        }
    }
}
