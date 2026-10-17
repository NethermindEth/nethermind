// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
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

    private static IEnumerable<TestCaseData> MalformedInputTestCases()
    {
        AbiSignature<UInt256> uint256 = new AbiSignature("f").With(AbiType.UInt256);
        AbiSignature<string> str = new AbiSignature("f").With(AbiType.String);

        yield return new TestCaseData(uint256, uint256.MethodId()[..2]).SetName("shorter than method id");
        yield return MalformedInput(uint256, "0102030405").SetName("truncated uint256");
        yield return MalformedInput(str,
            "000000000000000000000000000000000000000000000000000000000000ffff").SetName("string offset beyond input");
        yield return MalformedInput(str,
            "0000000000000000000000000000000000000000000000000000000000000020" +
            "00000000000000000000000000000000000000000000000000000000000003e8" +
            "6162630000000000000000000000000000000000000000000000000000000000").SetName("string length beyond input");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Bytes),
            "8000000000000000000000000000000000000000000000000000000000000000").SetName("bytes offset overflows int");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.UInt256)),
            "0000000000000000000000000000000000000000000000000000000000000020" +
            "8000000000000000000000000000000000000000000000000000000000000000").SetName("array length overflows int");
    }

    private static IEnumerable<TestCaseData> NonCanonicalOffsetTestCases()
    {
        AbiSignature<(string, string)> twoStrings = new AbiSignature("f").With(AbiType.String, AbiType.String);
        AbiSignature<string[]> fixedStrings = new AbiSignature("f").With(AbiType.Array(AbiType.String, 2));

        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.Bytes)),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first element
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to second element, aliasing the first
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to third element, aliasing the first
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the shared element
            "6162630000000000000000000000000000000000000000000000000000000000"   // "abc"
        ).SetName("bytes[] elements aliasing one tail");
        yield return MalformedInput(twoStrings,
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second string, aliasing the first
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of the shared string
            "68656c6c6f000000000000000000000000000000000000000000000000000000"   // "hello"
        ).SetName("tuple components aliasing one tail");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.String),
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to the string, one word past the head
            "0000000000000000000000000000000000000000000000000000000000000000" + // Gap
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of the string
            "68656c6c6f000000000000000000000000000000000000000000000000000000"   // "hello"
        ).SetName("gap before tail");
        yield return MalformedInput(twoStrings,
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to first string, pointing at the second tail
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second string, pointing at the first tail
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of second string
            "776f726c64000000000000000000000000000000000000000000000000000000" + // "world"
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first string
            "68656c6c6f000000000000000000000000000000000000000000000000000000"   // "hello"
        ).SetName("tails out of order");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.UInt256, AbiType.String),
            "0000000000000000000000000000000000000000000000000000000000000003" + // First argument, also read as the length of the string
            "0000000000000000000000000000000000000000000000000000000000000000"   // Offset to the string, pointing at the first head
        ).SetName("offset into the head area");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.Tuple(AbiType.UInt256, AbiType.String))),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to first element
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component of first element, `1`
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to second component of first element, one word past the head
            "0000000000000000000000000000000000000000000000000000000000000000" + // Gap
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second component of first element
            "6100000000000000000000000000000000000000000000000000000000000000"   // "a"
        ).SetName("gap before tail in tuple inside array");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Tuple(AbiType.UInt256, AbiType.Array(AbiType.String))),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component of the tuple, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component of the tuple
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second element, aliasing the first
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the shared element
            "6100000000000000000000000000000000000000000000000000000000000000"   // "a"
        ).SetName("string[] elements aliasing one tail inside tuple");
        yield return MalformedInput(fixedStrings,
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the fixed array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second element, aliasing the first
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the shared element
            "6100000000000000000000000000000000000000000000000000000000000000"   // "a"
        ).SetName("string[2] elements aliasing one tail");
        yield return MalformedInput(fixedStrings,
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the fixed array
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first element, one word past the head
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to second element
            "0000000000000000000000000000000000000000000000000000000000000000" + // Gap
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first element
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second element
            "6200000000000000000000000000000000000000000000000000000000000000"   // "b"
        ).SetName("gap before tail in string[2]");
    }

    private static IEnumerable<TestCaseData> TrailingDataTestCases()
    {
        AbiSignature<(uint, bool)> baz = new AbiSignature("baz").With(AbiType.UInt32, AbiType.Bool);
        AbiSignature<string> str = new AbiSignature("f").With(AbiType.String);

        const string bazBody =
            "0000000000000000000000000000000000000000000000000000000000000045" + // `69`
            "0000000000000000000000000000000000000000000000000000000000000001";  // `true`
        const string strBody =
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of the string
            "68656c6c6f000000000000000000000000000000000000000000000000000000";  // "hello"
        const string word = "0000000000000000000000000000000000000000000000000000000000000000";

        yield return MalformedInput(baz, bazBody + word).SetName("static arguments followed by a word");
        yield return MalformedInput(baz, bazBody + "00").SetName("static arguments followed by a byte");
        yield return MalformedInput(str, strBody + word).SetName("dynamic argument followed by a word");
        yield return MalformedInput(str, strBody + "00").SetName("dynamic argument followed by a byte");
    }

    private static TestCaseData MalformedInput<T>(AbiSignature<T> signature, string body) =>
        new(signature, Bytes.Concat(signature.MethodId(), Bytes.FromHexString(body)));

    [TestCaseSource(nameof(MalformedInputTestCases))]
    [TestCaseSource(nameof(NonCanonicalOffsetTestCases))]
    [TestCaseSource(nameof(TrailingDataTestCases))]
    public void Decode_MalformedInput<T>(AbiSignature<T> signature, byte[] input) =>
        Assert.Throws<AbiException>(() => AbiCodec.Decode(signature, input));

    [Test]
    public void Decode_MalformedInput_KeepsInnerException()
    {
        AbiSignature<UInt256> signature = new AbiSignature("f").With(AbiType.UInt256);

        Assert.That(() => AbiCodec.Decode(signature, new byte[2]),
            Throws.TypeOf<AbiException>().With.InnerException.TypeOf<ArgumentOutOfRangeException>());
    }

    private static IEnumerable<TestCaseData> AmplifyingInputTestCases()
    {
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.UInt256)),
            "0000000000000000000000000000000000000000000000000000000000000020" +
            "0000000000000000000000000000000000000000000000000000000000989680").SetName("uint256[] length beyond input");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.String)),
            "0000000000000000000000000000000000000000000000000000000000000020" +
            "0000000000000000000000000000000000000000000000000000000000989680").SetName("string[] length beyond input");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.Bytes)),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the array
            "0000000000000000000000000000000000000000000000000000000000000020" + // Length of the array, 32
            string.Concat(Enumerable.Repeat("0000000000000000000000000000000000000000000000000000000000000400", 32)) + // Offsets to all 32 elements, aliasing the first
            "0000000000000000000000000000000000000000000000000000000000000400" + // Length of the shared element, 1 KB
            new string('0', 2 * 1024) // Contents of the shared element
        ).SetName("bytes[] elements aliasing one 1 KB tail");
    }

    [TestCaseSource(nameof(AmplifyingInputTestCases))]
    public void Decode_AmplifyingInput_ThrowsBeforeAllocating<T>(AbiSignature<T> signature, byte[] input)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<AbiException>(() => AbiCodec.Decode(signature, input));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocated, Is.LessThan(16 * 1024));
    }
}
