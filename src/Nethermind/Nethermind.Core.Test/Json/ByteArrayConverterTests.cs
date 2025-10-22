// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

using System.Text;

[TestFixture]
public class ByteArrayConverterTests : ConverterTestBase<byte[]>
{
    [TestCase(null)]
    [TestCase(new byte[0])]
    [TestCase(new byte[] { 1 })]
    public void Test_roundtrip(byte[]? bytes)
    {
        TestConverter(bytes, static (before, after) => Bytes.AreEqual(before, after), new ByteArrayConverter());
    }

    [Test]
    public void Test_roundtrip_large()
    {
        ByteArrayConverter converter = new();
        for (var i = 0; i < 1024; i++)
        {
            byte[] bytes = new byte[i];
            for (var j = 0; j < i; j++)
            {
                bytes[j] = (byte)j;
            }

            TestConverter(bytes, static (before, after) => Bytes.AreEqual(before, after), converter);
        }
    }

    [Test]
    public void Direct_null()
    {
        IJsonSerializer serializer = new EthereumJsonSerializer();
        var result = serializer.Serialize<byte[]?>(null);
        result.Should().Be("null");
    }

    [TestCaseSource(nameof(ValidHexCases))]
    public void BareString_SegmentationInvariant_And_MatchesReference(string hex)
    {
        Exception? firstErr = null;
        byte[]? firstVal = null;
        bool firstSeen = false;

        byte[] json = Encoding.UTF8.GetBytes($"\"{hex}\"");
        byte[]? expected = ReferenceDecode(hex);
        foreach (ReadOnlySequence<byte> seq in Segmentations(json))
        {
            (byte[]? res, Exception? err) = InvokeOnBareString(seq);

            if (!firstSeen)
            {
                firstErr = err;
                firstVal = res;
                firstSeen = true;
            }
            else
            {
                if (firstErr is null && err is null)
                {
                    if (firstVal is null) res.Should().BeNull();
                    else res.Should().NotBeNull().And.Equal(firstVal);
                }
                else
                {
                    firstErr.Should().NotBeNull();
                    err.Should().NotBeNull();
                    err!.GetType().Should().Be(firstErr!.GetType());
                }
            }
        }

        if (firstErr is null)
        {
            if (expected is null) firstVal.Should().BeNull();
            else firstVal.Should().NotBeNull().And.Equal(expected);
        }
    }

    [TestCaseSource(nameof(ValidHexCases))]
    public void PropertyName_SegmentationInvariant_And_MatchesReference(string hex)
    {
        Exception? firstErr = null;
        byte[]? firstVal = null;
        bool firstSeen = false;

        byte[] json = Encoding.UTF8.GetBytes($"{{\"{hex}\":0}}");
        byte[]? expected = ReferenceDecode(hex);
        foreach (ReadOnlySequence<byte> seq in Segmentations(json))
        {
            (byte[]? res, Exception? err) = InvokeOnPropertyName(seq);

            if (!firstSeen)
            {
                firstErr = err;
                firstVal = res;
                firstSeen = true;
            }
            else
            {
                if (firstErr is null && err is null)
                {
                    if (firstVal is null) res.Should().BeNull();
                    else res.Should().NotBeNull().And.Equal(firstVal);
                }
                else
                {
                    firstErr.Should().NotBeNull();
                    err.Should().NotBeNull();
                    err!.GetType().Should().Be(firstErr!.GetType());
                }
            }
        }

        if (firstErr is null)
        {
            if (expected is null) firstVal.Should().BeNull();
            else firstVal.Should().NotBeNull().And.Equal(expected);
        }
    }

    [TestCaseSource(nameof(InvalidHexCases))]
    public void BareString_InvalidHex_ShouldThrowFormat(string hex)
    {
        var json = Encoding.UTF8.GetBytes($"\"{hex}\"");

        foreach (ReadOnlySequence<byte> seq in Segmentations(json))
        {
            (_, Exception? err) = InvokeOnBareString(seq);
            err.Should().NotBeNull();
            err.Should().BeOfType<FormatException>();
        }
    }

    [Test]
    public void BridgeAcrossBoundary_TwoNibbles_ShouldDecodeOneByte()
    {
        // "\"AB\"" => split exactly between 'A' and 'B' inside the string token
        var json = Encoding.UTF8.GetBytes("\"AB\"");
        // bytes: 0:'"', 1:'A', 2:'B', 3:'"'
        ReadOnlySequence<byte> seq = MakeSequence(json.AsMemory(0, 2), json.AsMemory(2)); // split at index 2 between 'A'|'B'

        (byte[]? res, Exception? err) = InvokeOnBareString(seq);
        err.Should().BeNull();
        res.Should().NotBeNull();
        res!.Length.Should().Be(1);
        res[0].Should().Be(0xAB);
    }

    [Test]
    public void NullLiteral_ReturnsNull()
    {
        ReadOnlySequence<byte> seq = JsonForLiteral("null");
        (byte[]? res, Exception? err) = InvokeRaw(seq);
        err.Should().BeNull();
        res.Should().BeNull();
    }

    [TestCase("true")]
    [TestCase("123")]
    [TestCase("{}")]
    public void NonStringTokens_ShouldThrowInvalidOperation(string literal)
    {
        ReadOnlySequence<byte> seq = JsonForLiteral(literal);
        (_, Exception? err) = InvokeRaw(seq);
        err.Should().NotBeNull();
        err.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void EmptyAfterPrefix_BehaviorIsConsistentAcrossSegmentation()
    {
        foreach (var hex in new[] { "0x", "0X" })
        {
            var json = Encoding.UTF8.GetBytes($"\"{hex}\"");

            byte[]? firstVal = null;
            Exception? firstErr = null;
            bool first = true;

            foreach (ReadOnlySequence<byte> seq in Segmentations(json))
            {
                (byte[]? res, Exception? err) = InvokeOnBareString(seq);
                if (first)
                {
                    firstVal = res; firstErr = err; first = false;
                }
                else
                {
                    if (firstErr is null && err is null)
                    {
                        // We accept either null or empty — but it must be consistent across segmentations.
                        if (firstVal is null) res.Should().BeNull();
                        else res.Should().NotBeNull().And.Equal(firstVal);
                    }
                    else
                    {
                        err.Should().NotBeNull();
                        firstErr.Should().NotBeNull();
                        err!.GetType().Should().Be(firstErr!.GetType());
                    }
                }
            }
        }
    }

    [Test]
    public void LongHex_AcrossManySplits_ShouldRoundTrip()
    {
        string body = string.Concat(Enumerable.Repeat("DEADBEEF", 2048)); // 16K hex chars
        string hex = "0x" + body;
        var json = Encoding.UTF8.GetBytes($"\"{hex}\"");

        foreach (ReadOnlySequence<byte> seq in Segmentations(json))
        {
            (byte[]? res, Exception? err) = InvokeOnBareString(seq);
            err.Should().BeNull();

            var expected = ReferenceDecode(hex);
            res.Should().NotBeNull().And.Equal(expected);
        }
    }

    [Test]
    public void Fuzz_RandomHex_SegmentationInvariant()
    {
        var rng = new Random(12345);
        for (int t = 0; t < 50; t++)
        {
            bool addPrefix = rng.Next(0, 2) == 1;
            int hexLen = rng.Next(0, 200); // number of hex digits (not bytes)
            Span<char> chars = hexLen <= 0 ? Span<char>.Empty : new char[hexLen];

            for (int i = 0; i < chars.Length; i++)
            {
                int v = rng.Next(0, 16);
                char c = "0123456789abcdef"[v];
                // randomly upper
                if (rng.Next(0, 2) == 1) c = char.ToUpperInvariant(c);
                chars[i] = c;
            }

            var s = (addPrefix ? "0x" : string.Empty) + new string(chars);
            var json = Encoding.UTF8.GetBytes($"\"{s}\"");
            var expected = ReferenceDecode(s);

            Exception? firstErr = null;
            byte[]? firstVal = null;
            bool firstSeen = false;

            foreach (ReadOnlySequence<byte> seq in Segmentations(json))
            {
                (byte[]? res, Exception? err) = InvokeOnBareString(seq);
                if (!firstSeen)
                {
                    firstErr = err; firstVal = res; firstSeen = true;
                }
                else
                {
                    if (firstErr is null && err is null)
                    {
                        if (firstVal is null) res.Should().BeNull();
                        else res.Should().NotBeNull().And.Equal(firstVal);
                    }
                    else
                    {
                        err.Should().NotBeNull();
                        firstErr.Should().NotBeNull();
                        err!.GetType().Should().Be(firstErr!.GetType());
                    }
                }
            }

            if (firstErr is null)
            {
                if (expected is null) firstVal.Should().BeNull();
                else firstVal.Should().NotBeNull().And.Equal(expected);
            }
        }
    }

    [Test]
    public void Test_DictionaryKey()
    {
        var random = new CryptoRandom();
        var dictionary = new Dictionary<byte[], int?>
        {
            { Bytes.FromHexString("0x0"), null },
            { Bytes.FromHexString("0x1"), random.NextInt(int.MaxValue) },
            { Build.An.Address.TestObject.Bytes, random.NextInt(int.MaxValue) },
            { random.GenerateRandomBytes(10), random.NextInt(int.MaxValue) },
            { random.GenerateRandomBytes(32), random.NextInt(int.MaxValue) },
        };

        TestConverter(dictionary, new ByteArrayConverter());
    }

    private static ReadOnlySequence<byte> JsonForLiteral(string literal) =>
        MakeSequence(Encoding.UTF8.GetBytes(literal));

    private static (byte[]? Result, Exception? Error) InvokeOnBareString(ReadOnlySequence<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        reader.Read().Should().BeTrue();
        reader.TokenType.Should().Be(JsonTokenType.String);

        try { return (ByteArrayConverter.Convert(ref reader), null); }
        catch (Exception ex) { return (null, ex); }
    }

    private static (byte[]? Result, Exception? Error) InvokeOnPropertyName(ReadOnlySequence<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        reader.Read().Should().BeTrue();
        reader.TokenType.Should().Be(JsonTokenType.StartObject);
        reader.Read().Should().BeTrue();
        reader.TokenType.Should().Be(JsonTokenType.PropertyName);

        try { return (ByteArrayConverter.Convert(ref reader), null); }
        catch (Exception ex) { return (null, ex); }
    }

    private static (byte[]? Result, Exception? Error) InvokeRaw(ReadOnlySequence<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        reader.Read().Should().BeTrue();
        try { return (ByteArrayConverter.Convert(ref reader), null); }
        catch (Exception ex) { return (null, ex); }
    }

    private static byte[]? ReferenceDecode(string s)
    {
        if (s.Length == 0)
            return null;

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);

        static int Nibble(char c)
        {
            char d = (char)(c | 0x20);
            if (d >= '0' && d <= '9') return d - '0';
            if (d >= 'a' && d <= 'f') return d - 'a' + 10;
            throw new FormatException("Invalid hex char");
        }

        int odd = s.Length & 1;
        var output = new byte[(s.Length >> 1) + odd];
        int oi = 0, i = 0;

        if (odd == 1)
            output[oi++] = (byte)Nibble(s[i++]);

        for (; i < s.Length; i += 2)
            output[oi++] = (byte)((Nibble(s[i]) << 4) | Nibble(s[i + 1]));

        return output;
    }

    private static ReadOnlySequence<byte> MakeSequence(params ReadOnlyMemory<byte>[] parts)
    {
        if (parts.Length == 1)
            return new ReadOnlySequence<byte>(parts[0]);

        var first = new BufferSegment(parts[0]);
        BufferSegment last = first;
        for (int i = 1; i < parts.Length; i++)
            last = last.Append(parts[i]);

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static IEnumerable<ReadOnlySequence<byte>> Segmentations(byte[] data)
    {
        // 1) single contiguous
        yield return MakeSequence(data);

        // 2) all two-way splits
        for (int i = 1; i < data.Length; i++)
            yield return MakeSequence(data.AsMemory(0, i), data.AsMemory(i));

        // 3) triples (combinatorial for short inputs, representative for long)
        if (data.Length <= 32)
        {
            for (int i = 1; i < data.Length - 1; i++)
                for (int j = i + 1; j < data.Length; j++)
                    yield return MakeSequence(
                        data.AsMemory(0, i),
                        data.AsMemory(i, j - i),
                        data.AsMemory(j));
        }
        else
        {
            int[] cuts = new[] { 1, 2, 7, data.Length / 2, data.Length - 3, data.Length - 1 }
                .Distinct().Where(k => k > 0 && k < data.Length).OrderBy(k => k).ToArray();

            for (int a = 0; a + 1 < cuts.Length; a++)
            {
                int i = cuts[a], j = cuts[a + 1];
                yield return MakeSequence(
                    data.AsMemory(0, i),
                    data.AsMemory(i, j - i),
                    data.AsMemory(j));
            }
        }
    }

    public static IEnumerable<object[]> ValidHexCases() => new object[][]
    {
        [""],              // empty => null (after trim below)
        ["0x"],            // empty after prefix
        ["0X"],            // same as above, tests case-insens prefix
        ["F"],             // odd, single nibble => [0F]
        ["f"],
        ["0xF"],
        ["0XF"],
        ["1f"],            // even
        ["1F"],
        ["0x1f"],
        ["0X1F"],
        ["0x1fF"],
        ["0X1Ff"],
        ["123"],           // odd + pairs
        ["0x123"],
        ["DEADBEEF"],      // classic even
        ["0xDEADBEEF"],
        ["deadBEEF"],
        ["0xdeadBEEF"],
    };

    public static IEnumerable<object[]> InvalidHexCases() => new object[][]
    {
        ["G"],
        ["0xG1"],
        ["1G"],
        ["0x1G"],
        ["-"],
        ["zz"],
        ["0xzz"],
    };

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            BufferSegment next = new(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = next;
            return next;
        }
    }
}
