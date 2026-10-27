// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Abi.Test;

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
        yield return new TestCaseData(AbiType.UInt32, 3, new[] { 1u, 2u }).SetName("expected 3, got 2");
        yield return new TestCaseData(AbiType.UInt32, 2, new[] { 1u, 2u, 3u }).SetName("expected 2, got 3");
        yield return new TestCaseData(AbiType.UInt32, 4, Array.Empty<uint>()).SetName("expected 4, got 0");
        yield return new TestCaseData(AbiType.UInt32, 1, new[] { 42u, 43u }).SetName("expected 1, got 2");
        yield return new TestCaseData(AbiType.UInt32, 0, new[] { 99u }).SetName("expected 0, got 1");
        yield return new TestCaseData(AbiType.String, 2, new[] { "a", "b", "c" }).SetName("dynamic elements, expected 2, got 3");
    }

    [TestCaseSource(nameof(ArrayKTestCases))]
    public void ArrayK_EncodeWrongLength<T>(Abi<T> element, int length, T[] elements)
    {
        AbiSignature<T[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(element, length)
            );

        Assert.Throws<AbiException>(() => AbiCodec.Encode(signature, elements));
    }

    private static IEnumerable<TestCaseData> HeadSizeOutOfRangeTestCases()
    {
        yield return ThrowsOnBuild<OverflowException>(() => AbiType.Array(AbiType.UInt256, int.MaxValue)).SetName("uint256[2^31 - 1]");
        yield return ThrowsOnBuild<OverflowException>(() => AbiType.Array(AbiType.Array(AbiType.UInt256, 1 << 20), 1 << 6)).SetName("uint256[2^20][2^6]");
        yield return ThrowsOnBuild<OverflowException>(() => AbiType.Array(AbiType.String, 1 << 26)).SetName("string[2^26]");
        yield return ThrowsOnBuild<OverflowException>(() => AbiType.Tuple(AbiType.Array(AbiType.UInt256, 1 << 25), AbiType.Array(AbiType.UInt256, 1 << 25))).SetName("(uint256[2^25],uint256[2^25])");
        yield return ThrowsOnBuild<ArgumentOutOfRangeException>(() => AbiType.Array(AbiType.UInt256, -1)).SetName("uint256[-1]");
    }

    private static IEnumerable<TestCaseData> BytesMOutOfRangeTestCases()
    {
        yield return ThrowsOnBuild<ArgumentOutOfRangeException>(() => AbiType.BytesM(0)).SetName("bytes0");
        yield return ThrowsOnBuild<ArgumentOutOfRangeException>(() => AbiType.BytesM(33)).SetName("bytes33");
        yield return ThrowsOnBuild<ArgumentOutOfRangeException>(() => AbiType.Array(AbiType.BytesM(-32), 1 << 26)).SetName("bytes-32[2^26]");
    }

    private static IEnumerable<TestCaseData> CustomTypeTestCases()
    {
        yield return ThrowsOnBuild<ArgumentOutOfRangeException>(() => new Abi<byte>
        {
            Name = "custom",
            Read = AbiType.UInt8.Read,
            Write = AbiType.UInt8.Write,
            HeadSize = -32
        }).SetName("custom with head size -32");
    }

    private static IEnumerable<TestCaseData> ZeroSizeElementTestCases()
    {
        yield return ThrowsOnBuild<ArgumentOutOfRangeException>(() => AbiType.Array(AbiType.Tuple())).SetName("()[]");
        yield return ThrowsOnBuild<ArgumentOutOfRangeException>(() => AbiType.Array(AbiType.Array(AbiType.UInt256, 0))).SetName("uint256[0][]");
    }

    private static TestCaseData ThrowsOnBuild<TException>(Action build) where TException : Exception =>
        new(build, typeof(TException));

    [TestCaseSource(nameof(HeadSizeOutOfRangeTestCases))]
    [TestCaseSource(nameof(BytesMOutOfRangeTestCases))]
    [TestCaseSource(nameof(CustomTypeTestCases))]
    [TestCaseSource(nameof(ZeroSizeElementTestCases))]
    public void Build_InvalidType(Action build, Type exception) =>
        Assert.That(build, Throws.TypeOf(exception));

    private static IEnumerable<TestCaseData> SizeOverflowTestCases()
    {
        Abi<UInt256[][]> uint256Arrays = AbiType.Array(AbiType.Array(AbiType.UInt256, 1 << 20));
        UInt256[][] nearMaxSize = new UInt256[63][];

        yield return SizeOverflow(uint256Arrays, new UInt256[1 << 6][]).SetName("uint256[2^20][] of 2^6 elements");
        yield return SizeOverflow(AbiType.Array(uint256Arrays), [nearMaxSize, nearMaxSize]).SetName("uint256[2^20][][] of two 63-element arrays");
        yield return SizeOverflow(AbiType.Array(uint256Arrays, 2), [nearMaxSize, nearMaxSize]).SetName("uint256[2^20][][2] of 63-element arrays");
        yield return SizeOverflow(AbiType.Tuple(uint256Arrays, uint256Arrays), (nearMaxSize, nearMaxSize)).SetName("(uint256[2^20][],uint256[2^20][]) of 63-element arrays");
        yield return SizeOverflow(AbiType.Tuple(AbiType.Array(AbiType.Array(AbiType.UInt256, (1 << 25) - 1))), new UInt256[2][]).SetName("(uint256[2^25 - 1][]) of 2 elements");
    }

    private static TestCaseData SizeOverflow<T>(Abi<T> abi, T value) => new(abi, value);

    [TestCaseSource(nameof(SizeOverflowTestCases))]
    public void Size_Overflow<T>(Abi<T> abi, T value) =>
        Assert.That(() => abi.Size(value), Throws.TypeOf<OverflowException>());

    [Test]
    public void Encode_SizeOverflow_KeepsInnerException()
    {
        AbiSignature<UInt256[][]> signature = new AbiSignature("f").With(AbiType.Array(AbiType.Array(AbiType.UInt256, 1 << 20)));
        UInt256[][] value = new UInt256[1 << 7][]; // 2^7 * 2^25 = 2^32 wraps to zero in unchecked arithmetic

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => AbiCodec.Encode(signature, value),
                Throws.TypeOf<AbiException>().With.InnerException.TypeOf<OverflowException>());
            Assert.That(() => AbiCodec.Encode(signature, value, new byte[1024]),
                Throws.TypeOf<AbiException>().With.InnerException.TypeOf<OverflowException>());
        }
    }

    private static IEnumerable<TestCaseData> LoneSurrogateTestCases()
    {
        yield return new TestCaseData("\uD800").SetName("lone high surrogate");
        yield return new TestCaseData("a\uDC00b").SetName("lone low surrogate inside text");
    }

    [TestCaseSource(nameof(LoneSurrogateTestCases))]
    public void Encode_LoneSurrogate(string value)
    {
        AbiSignature<string> signature = new AbiSignature("f").With(AbiType.String);

        Assert.That(() => AbiCodec.Encode(signature, value),
            Throws.TypeOf<AbiException>().With.InnerException.TypeOf<EncoderFallbackException>());
    }

    [Test]
    public void Mismatched_MethodId()
    {
        AbiSignature<uint> signature = new AbiSignature("f")
            .With(
                AbiType.UInt32
            );

        byte[] encoded = AbiCodec.Encode(signature, 69u);

        encoded[0] ^= 1;

        Assert.Throws<AbiException>(() => AbiCodec.Decode(signature, encoded));
    }

    private static IEnumerable<TestCaseData> MalformedInputTestCases()
    {
        AbiSignature<UInt256> uint256 = new AbiSignature("f").With(AbiType.UInt256);
        AbiSignature<string> str = new AbiSignature("f").With(AbiType.String);
        AbiSignature<bool> boolean = new AbiSignature("f").With(AbiType.Bool);

        yield return new TestCaseData(boolean, Array.Empty<byte>()).SetName("empty input");
        yield return MalformedInput(boolean, "").SetName("method id without arguments");
        yield return new TestCaseData(uint256, uint256.MethodId[..2].ToArray()).SetName("shorter than method id");
        yield return MalformedInput(uint256, "0102030405").SetName("truncated uint256");
        yield return MalformedInput(str,
            "000000000000000000000000000000000000000000000000000000000000ffff").SetName("string offset beyond input");
        yield return MalformedInput(str,
            "0000000000000000000000000000000000000000000000000000000000000020" +
            "00000000000000000000000000000000000000000000000000000000000003e8" +
            "6162630000000000000000000000000000000000000000000000000000000000").SetName("string length beyond input");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Bytes),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the bytes
            "0000000000000000000000000000000000000000000000000000000000000001"   // Length of the bytes, with no contents after it
        ).SetName("bytes length one beyond input");
        yield return MalformedInput(new AbiSignature("DepositEvent").With(AbiType.Bytes, AbiType.Bytes, AbiType.Bytes, AbiType.Bytes, AbiType.Bytes),
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to first argument
            "0000000000000000000000000000000000000000000000000000000000000000" + // Offset to second argument
            "0000000000000000000000000000000000000000000000000000000000000000" + // Offset to third argument
            "0000000000000000000000000000000000000000000000000000000000000000" + // Offset to fourth argument
            "0000000000000000000000000000000000000000000000000000000000000000" + // Offset to fifth argument
            "00000000000000000000000000000000000000000000000000000000000000c8" + // Length of first argument, `200`
            "0000000000000000000000000000000000000000000000000000000000000000" + // Contents of first argument, 64 of the declared 200 bytes
            "0000000000000000000000000000000000000000000000000000000000000000"   // Contents of first argument, continued
        ).SetName("bytes length beyond input among five arguments");
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

    private static IEnumerable<TestCaseData> NonCanonicalValueTestCases()
    {
        AbiSignature<bool> boolean = new AbiSignature("f").With(AbiType.Bool);
        AbiSignature<sbyte> int8 = new AbiSignature("f").With(AbiType.Int8);

        yield return MalformedInput(boolean,
            "0000000000000000000000000000000000000000000000000000000000000002").SetName("bool of 2");
        yield return MalformedInput(boolean,
            "0000000000000000000000000000000000000000000000000000000000000100").SetName("bool with byte 30 set");
        yield return MalformedInput(boolean,
            "8000000000000000000000000000000000000000000000000000000000000001").SetName("bool with high bit and low bit set");
        yield return MalformedInput(int8,
            "0000000000000000000000000000000000000000000000000000000000000080").SetName("int8 of 0x80 without sign extension");
        yield return MalformedInput(int8,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f").SetName("int8 of -129");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Int64),
            "000000000000000000000000000000000000000000000000ffffffffffffffff").SetName("int64 of -1 without sign extension");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.UInt64),
            "0000000000000000000000000000000000000000000000010000000000000000").SetName("uint64 of 2^64");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Address),
            "0100000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d").SetName("address with byte 0 set");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.BytesM(4)),
            "deadbeef00000000000000000000000000000000000000000000000000000001").SetName("bytes4 with non-zero padding");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Bytes),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the bytes
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the bytes
            "aa01000000000000000000000000000000000000000000000000000000000000"   // 0xaa followed by non-zero padding
        ).SetName("bytes with non-zero padding");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.String),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the string
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of the string
            "6101000000000000000000000000000000000000000000000000000000000000"   // "a" followed by non-zero padding
        ).SetName("string with non-zero padding");

        (string Utf8, string Description)[] invalidUtf8 =
        [
            ("ff", "invalid UTF-8 byte 0xff"),
            ("c328", "invalid UTF-8 continuation 0xc3 0x28"),
            ("c0af", "overlong UTF-8 encoding 0xc0 0xaf"),
            ("eda080", "UTF-8 encoded surrogate 0xed 0xa0 0x80"),
        ];
        foreach ((string utf8, string description) in invalidUtf8)
        {
            yield return MalformedInput(new AbiSignature("f").With(AbiType.String),
                "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the string
                (utf8.Length / 2).ToString("x64") +                                   // Length of the string
                utf8.PadRight(64, '0')                                                // Invalid UTF-8 followed by zero padding
            ).SetName($"string with {description}");
        }
    }

    private static TestCaseData MalformedInput<T>(AbiSignature<T> signature, string body) =>
        new(signature, Bytes.Concat(signature.MethodId, Bytes.FromHexString(body)));

    [TestCaseSource(nameof(MalformedInputTestCases))]
    [TestCaseSource(nameof(NonCanonicalOffsetTestCases))]
    [TestCaseSource(nameof(TrailingDataTestCases))]
    [TestCaseSource(nameof(NonCanonicalValueTestCases))]
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
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.UInt256)),
            "0000000000000000000000000000000000000000000000000000000000000020" +
            "0000000000000000000000000000000000000000000000000000000008000000").SetName("uint256[] head size overflows int");
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
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.Array(AbiType.UInt256))),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the array
            "0000000000000000000000000000000000000000000000000000000000000080" + // Length of the array, 128
            string.Concat(Enumerable.Repeat("0000000000000000000000000000000000000000000000000000000000001000", 128)) + // Offsets to all 128 elements, aliasing the first
            "0000000000000000000000000000000000000000000000000000000000000080" + // Length of the shared element, 128
            new string('0', 2 * 128 * 32) // Contents of the shared element
        ).SetName("uint256[][] elements aliasing one 4 KB tail");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.Array(AbiType.UInt256, 2))),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the array
            "0000000000000000000000000000000000000000000000000000000000002710" + // Length of the array, 10_000
            new string('0', 2 * 320_000) // One word per element, half of what 10_000 `uint256[2]` elements need
        ).SetName("uint256[2][] length beyond input at two words per element");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.Tuple(
                AbiType.UInt256, AbiType.UInt256, AbiType.UInt256, AbiType.UInt256, AbiType.UInt256, AbiType.UInt256, AbiType.Bytes))),
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the array
            "00000000000000000000000000000000000000000000000000000000000003e8" + // Length of the array, 1000
            string.Concat(Enumerable.Repeat("0000000000000000000000000000000000000000000000000000000000007d00", 1000)) // Offsets past the end, with no tails
        ).SetName("(uint256,uint256,uint256,uint256,uint256,uint256,bytes)[] offsets with no tails");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.String),
            "0000000000000000000000000000000000000000000000000000000000000020" +
            "00000000000000000000000000000000000000000000000000000000000f4240").SetName("string length 10^6 beyond input");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.Bool, 10_000_000)),
            "0000000000000000000000000000000000000000000000000000000000000000").SetName("bool[10^7] beyond input");
        yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.String, 1_000_000)),
            "0000000000000000000000000000000000000000000000000000000000000020" // Offset to the array, with no element offsets after it
        ).SetName("string[10^6] beyond input");

        (string Length, string Description)[] lengths =
        [
            ("00000000000000000000000000000000000000000000000000000000000f4240", "10^6"),
            ("000000000000000000000000000000000000000000000000000000007fffffff", "2^31 - 1"),
            ("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "2^256 - 1"),
        ];
        foreach ((string length, string description) in lengths)
        {
            yield return MalformedInput(new AbiSignature("f").With(AbiType.Bytes),
                "0000000000000000000000000000000000000000000000000000000000000020" + length).SetName($"bytes length {description} beyond input");
            yield return MalformedInput(new AbiSignature("f").With(AbiType.Array(AbiType.Bool)),
                "0000000000000000000000000000000000000000000000000000000000000020" + length).SetName($"bool[] length {description} beyond input");
        }
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
