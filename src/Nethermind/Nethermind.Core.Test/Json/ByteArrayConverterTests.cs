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
    public void Test_roundtrip(byte[]? bytes) => TestConverter(bytes, static (before, after) => Bytes.AreEqual(before, after), new ByteArrayConverter());

    [Test]
    public void Test_roundtrip_large()
    {
        ByteArrayConverter converter = new();
        for (int i = 0; i < 1024; i++)
        {
            byte[] bytes = new byte[i];
            for (int j = 0; j < i; j++)
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
        string result = serializer.Serialize<byte[]?>(null);
        result.Should().Be("null");
    }

    [TestCaseSource(nameof(ValidHexCases))]
    public void BareString_SegmentationInvariant_And_MatchesReference(string hex)
    {
        byte[] json = Encoding.UTF8.GetBytes($"\"{hex}\"");
        byte[]? expected = ReferenceDecode(hex);
        AssertSegmentationInvariant(json, expected, InvokeOnBareString);
    }

    [TestCaseSource(nameof(ValidHexCases))]
    public void PropertyName_SegmentationInvariant_And_MatchesReference(string hex)
    {
        byte[] json = Encoding.UTF8.GetBytes($"{{\"{hex}\":0}}");
        byte[]? expected = ReferenceDecode(hex);
        AssertSegmentationInvariant(json, expected, InvokeOnPropertyName);
    }

    [TestCaseSource(nameof(InvalidHexCases))]
    public void BareString_InvalidHex_ShouldThrowFormat(string hex)
    {
        byte[] json = Encoding.UTF8.GetBytes($"\"{hex}\"");

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
        byte[] json = Encoding.UTF8.GetBytes("\"AB\"");
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

    [TestCase("0x")]
    [TestCase("0X")]
    public void EmptyAfterPrefix_BehaviorIsConsistentAcrossSegmentation(string hex)
    {
        byte[] json = Encoding.UTF8.GetBytes($"\"{hex}\"");
        // We accept either null or empty — but it must be consistent across segmentations.
        // Pass null as expected to skip the reference check, only assert consistency.
        AssertSegmentationConsistency(json, InvokeOnBareString);
    }

    [Test]
    public void LongHex_AcrossManySplits_ShouldRoundTrip()
    {
        string body = string.Concat(Enumerable.Repeat("DEADBEEF", 2048)); // 16K hex chars
        string hex = "0x" + body;
        byte[] json = Encoding.UTF8.GetBytes($"\"{hex}\"");
        byte[]? expected = ReferenceDecode(hex);

        foreach (ReadOnlySequence<byte> seq in Segmentations(json))
        {
            (byte[]? res, Exception? err) = InvokeOnBareString(seq);
            err.Should().BeNull();
            res.Should().NotBeNull().And.Equal(expected);
        }
    }

    [Test]
    public void Fuzz_RandomHex_SegmentationInvariant()
    {
        Random rng = new(12345);
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

            string s = (addPrefix ? "0x" : string.Empty) + new string(chars);
            byte[] json = Encoding.UTF8.GetBytes($"\"{s}\"");
            byte[]? expected = ReferenceDecode(s);

            AssertSegmentationInvariant(json, expected, InvokeOnBareString);
        }
    }

    [TestCase(new byte[] { 0xab, 0xcd }, true, true, "\"0xabcd\"")]
    [TestCase(new byte[] { 0xab, 0xcd }, false, true, "\"0xabcd\"")]
    [TestCase(new byte[] { 0x00, 0xab }, true, true, "\"0xab\"")]
    [TestCase(new byte[] { 0x00, 0xab }, false, true, "\"0x00ab\"")]
    [TestCase(new byte[] { 0x00, 0x00 }, true, true, "\"0x0\"")]
    [TestCase(new byte[] { 0x00, 0x00 }, false, true, "\"0x0000\"")]
    [TestCase(new byte[] { 0xab }, true, false, "\"ab\"")]
    [TestCase(new byte[] { 0xab }, false, false, "\"ab\"")]
    [TestCase(new byte[] { 0x0a }, true, true, "\"0xa\"")]
    [TestCase(new byte[] { 0x0a }, false, true, "\"0x0a\"")]
    public void Write_OutputFormat(byte[] input, bool skipLeadingZeros, bool addHexPrefix, string expected)
    {
        using System.IO.MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);
        ByteArrayConverter.Convert(writer, input, skipLeadingZeros, addHexPrefix);
        writer.Flush();
        Encoding.UTF8.GetString(ms.ToArray()).Should().Be(expected);
    }

    [Test]
    public void Write_LargeOutput_UsesArrayPool()
    {
        // 200 bytes = 400 hex chars + "0x" prefix + quotes > 256 byte InlineArray threshold
        byte[] input = new byte[200];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(i & 0xFF);

        using System.IO.MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);
        ByteArrayConverter.Convert(writer, input, skipLeadingZeros: false);
        writer.Flush();
        string output = Encoding.UTF8.GetString(ms.ToArray());
        output.Should().StartWith("\"0x");
        output.Should().EndWith("\"");
        output.Length.Should().Be(404); // 400 hex + 2 prefix + 2 quotes
    }

    [TestCase(new byte[] { 0xab, 0xcd }, "{\"0xabcd\":1}")]
    [TestCase(new byte[] { 0x00, 0x00 }, "{\"0x0000\":1}")]
    public void WriteAsPropertyName(byte[] input, string expected)
    {
        ByteArrayConverter converter = new();
        using System.IO.MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);
        writer.WriteStartObject();
        converter.WriteAsPropertyName(writer, input, JsonSerializerOptions.Default);
        writer.WriteNumberValue(1);
        writer.WriteEndObject();
        writer.Flush();
        Encoding.UTF8.GetString(ms.ToArray()).Should().Be(expected);
    }

    [Test]
    public void Test_DictionaryKey()
    {
        CryptoRandom random = new();
        Dictionary<byte[], int?> dictionary = new()
        {
            { Bytes.FromHexString("0x0"), null },
            { Bytes.FromHexString("0x1"), random.NextInt(int.MaxValue) },
            { Build.An.Address.TestObject.Bytes, random.NextInt(int.MaxValue) },
            { random.GenerateRandomBytes(10), random.NextInt(int.MaxValue) },
            { random.GenerateRandomBytes(32), random.NextInt(int.MaxValue) },
        };

        TestConverter(dictionary, new ByteArrayConverter());
    }

    /// <summary>
    /// Asserts that all segmentations of <paramref name="json"/> produce consistent results
    /// and match the <paramref name="expected"/> reference value.
    /// </summary>
    private static void AssertSegmentationInvariant(
        byte[] json,
        byte[]? expected,
        Func<ReadOnlySequence<byte>, (byte[]? Result, Exception? Error)> invoke)
    {
        Exception? firstErr = null;
        byte[]? firstVal = null;
        bool firstSeen = false;

        foreach (ReadOnlySequence<byte> seq in Segmentations(json))
        {
            (byte[]? res, Exception? err) = invoke(seq);

            if (!firstSeen)
            {
                firstErr = err;
                firstVal = res;
                firstSeen = true;
            }
            else
            {
                AssertResultsConsistent(firstVal, firstErr, res, err);
            }
        }

        if (firstErr is null)
        {
            if (expected is null) firstVal.Should().BeNull();
            else firstVal.Should().NotBeNull().And.Equal(expected);
        }
    }

    /// <summary>
    /// Asserts that all segmentations produce consistent results (without checking a reference value).
    /// </summary>
    private static void AssertSegmentationConsistency(
        byte[] json,
        Func<ReadOnlySequence<byte>, (byte[]? Result, Exception? Error)> invoke)
    {
        byte[]? firstVal = null;
        Exception? firstErr = null;
        bool first = true;

        foreach (ReadOnlySequence<byte> seq in Segmentations(json))
        {
            (byte[]? res, Exception? err) = invoke(seq);
            if (first)
            {
                firstVal = res;
                firstErr = err;
                first = false;
            }
            else
            {
                AssertResultsConsistent(firstVal, firstErr, res, err);
            }
        }
    }

    private static void AssertResultsConsistent(byte[]? firstVal, Exception? firstErr, byte[]? res, Exception? err)
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

    private static ReadOnlySequence<byte> JsonForLiteral(string literal) =>
        MakeSequence(Encoding.UTF8.GetBytes(literal));

    private static (byte[]? Result, Exception? Error) InvokeOnBareString(ReadOnlySequence<byte> json)
    {
        Utf8JsonReader reader = new(json);
        reader.Read().Should().BeTrue();
        reader.TokenType.Should().Be(JsonTokenType.String);

        try { return (ByteArrayConverter.Convert(ref reader), null); }
        catch (Exception ex) { return (null, ex); }
    }

    private static (byte[]? Result, Exception? Error) InvokeOnPropertyName(ReadOnlySequence<byte> json)
    {
        Utf8JsonReader reader = new(json);
        reader.Read().Should().BeTrue();
        reader.TokenType.Should().Be(JsonTokenType.StartObject);
        reader.Read().Should().BeTrue();
        reader.TokenType.Should().Be(JsonTokenType.PropertyName);

        try { return (ByteArrayConverter.Convert(ref reader), null); }
        catch (Exception ex) { return (null, ex); }
    }

    private static (byte[]? Result, Exception? Error) InvokeRaw(ReadOnlySequence<byte> json)
    {
        Utf8JsonReader reader = new(json);
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
        byte[] output = new byte[(s.Length >> 1) + odd];
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

        BufferSegment first = new(parts[0]);
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


    [TestCaseSource(nameof(StrictHexCases))]
    public void StrictConverter_HexCases(string name, string hex, object expected)
    {
        byte[] json = Encoding.UTF8.GetBytes($"\"{hex}\"");

        foreach (ReadOnlySequence<byte> seq in Segmentations(json))
        {
            Utf8JsonReader reader = new(seq);
            reader.Read();

            try
            {
                byte[]? result = new StrictHexByteArrayConverter()
                    .Read(ref reader, typeof(byte[]), JsonSerializerOptions.Default);

                Assert.That(result, Is.EqualTo((byte[])expected));
            }
            catch (JsonException ex)
            {
                Assert.That(ex.Message, Is.EqualTo((string)expected));
            }
        }
    }

    public static IEnumerable<TestCaseData> StrictHexCases()
    {
        yield return new("Rejects_SingleNibble", "0xF", Bytes.ErrOddLength);
        yield return new("Rejects_ThreeDigits_Numeric", "0x123", Bytes.ErrOddLength);
        yield return new("Rejects_ThreeDigits_MixedCase", "0x1fF", Bytes.ErrOddLength);
        yield return new("Rejects_ThreeDigits_Alpha", "0xabc", Bytes.ErrOddLength);

        yield return new("Rejects_NoPrefix_SingleDigit", "F", Bytes.ErrMissingPrefix);
        yield return new("Rejects_NoPrefix_Numeric", "123", Bytes.ErrMissingPrefix);
        yield return new("Rejects_NoPrefix_Alpha", "abc", Bytes.ErrMissingPrefix);
        yield return new("Rejects_NoPrefix_Byte", "1f", Bytes.ErrMissingPrefix);
        yield return new("Rejects_NoPrefix_LongHex", "DEADBEEF", Bytes.ErrMissingPrefix);

        yield return new("Rejects_InvalidPrefixFormat", "0xxx", Bytes.ErrSyntax);
        yield return new("Rejects_InvalidHexCharacters", "0x01zz01", Bytes.ErrSyntax);

        yield return new("Parses_EmptyHex", "0x", Array.Empty<byte>());
        yield return new("Parses_SingleByte", "0x1f", new byte[] { 0x1f });
        yield return new("Parses_DeadBeef", "0xDEADBEEF", new byte[] { 0xde, 0xad, 0xbe, 0xef });
    }
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
