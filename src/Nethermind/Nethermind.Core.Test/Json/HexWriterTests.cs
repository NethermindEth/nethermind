// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Utf8 = System.Text.Encoding;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class HexWriterTests
{
    private const string ZeroWord = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string PatternWord = "00070e151c232a31383f464d545b626970777e858c939aa1a8afb6bdc4cbd2d9";
    private const string OnesWord = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private static string WriteToString(Action<Utf8JsonWriter> writeAction)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { SkipValidation = true }))
        {
            writeAction(writer);
        }
        return Utf8.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string WriteToString(Action<ArrayBufferWriter<byte>> writeAction)
    {
        ArrayBufferWriter<byte> buffer = new();
        writeAction(buffer);
        return Utf8.UTF8.GetString(buffer.WrittenSpan);
    }

    private static byte[] HexBytes(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        return bytes;
    }

    private static UInt256 UInt256FromHex(string hex)
    {
        if (hex == "max")
        {
            return UInt256.MaxValue;
        }

        return new UInt256(HexBytes((hex.Length & 1) == 0 ? hex : "0" + hex), isBigEndian: true);
    }

    [TestCase(ZeroWord, false, "\"" + ZeroWord + "\"", TestName = "Fixed32_NoPrefix_AllZeros")]
    [TestCase(PatternWord, false, "\"" + PatternWord + "\"", TestName = "Fixed32_NoPrefix_Pattern")]
    [TestCase(OnesWord, false, "\"" + OnesWord + "\"", TestName = "Fixed32_NoPrefix_AllOnes")]
    [TestCase(ZeroWord, true, "\"0x" + ZeroWord + "\"", TestName = "Fixed32_WithPrefix_AllZeros")]
    [TestCase(PatternWord, true, "\"0x" + PatternWord + "\"", TestName = "Fixed32_WithPrefix_Pattern")]
    public void WriteFixed32HexRawValue_PrefixVariants(string inputHex, bool addPrefix, string expected)
    {
        byte[] data = HexBytes(inputHex);
        string actual = WriteToString(w => HexWriter.WriteFixed32HexRawValue(w, data, addHexPrefix: addPrefix));
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(0x0UL, true, true, "0x" + ZeroWord, TestName = "U256Raw_ZeroPaddedWithPrefix_Zero")]
    [TestCase(0x0UL, false, true, "0x0", TestName = "U256Raw_TrimmedWithPrefix_Zero")]
    [TestCase(0x1UL, true, true, "0x" + "000000000000000000000000000000000000000000000000000000000000000" + "1", TestName = "U256Raw_ZeroPaddedWithPrefix_One")]
    [TestCase(0x1UL, false, true, "0x1", TestName = "U256Raw_TrimmedWithPrefix_One")]
    [TestCase(0x5UL, false, true, "0x5", TestName = "U256Raw_Trimmed_SingleNibble")]
    [TestCase(0xabUL, false, true, "0xab", TestName = "U256Raw_Trimmed_TwoNibbles")]
    [TestCase(0x1234567UL, false, true, "0x1234567", TestName = "U256Raw_Trimmed_OddNibbles")]
    [TestCase(ulong.MaxValue, false, true, "0xffffffffffffffff", TestName = "U256Raw_Trimmed_UlongMax")]
    [TestCase(0x1UL, false, false, "1", TestName = "U256Raw_TrimmedNoPrefix_One")]
    [TestCase(0x0UL, false, false, "0", TestName = "U256Raw_TrimmedNoPrefix_Zero")]
    [TestCase(0x1UL, true, false, "000000000000000000000000000000000000000000000000000000000000000" + "1", TestName = "U256Raw_ZeroPaddedNoPrefix_One")]
    public void WriteUInt256HexRawValue_AllVariants(ulong value, bool zeroPadded, bool addPrefix, string expectedBody)
    {
        string actual = WriteToString(w => HexWriter.WriteUInt256HexRawValue(w, (UInt256)value, zeroPadded, addPrefix));
        Assert.That(actual, Is.EqualTo("\"" + expectedBody + "\""));
    }

    [Test]
    public void WriteUInt256HexRawValue_ZeroPaddedMaxValue_NoPrefix_64f()
    {
        string actual = WriteToString(w => HexWriter.WriteUInt256HexRawValue(w, UInt256.MaxValue, zeroPadded: true, addHexPrefix: false));
        Assert.That(actual, Is.EqualTo("\"" + OnesWord + "\""));
    }

    [TestCase("0", true, true, "0x" + ZeroWord, TestName = "U256Buffer_ZeroPaddedWithPrefix_Zero")]
    [TestCase("1", true, true, "0x" + "000000000000000000000000000000000000000000000000000000000000000" + "1", TestName = "U256Buffer_ZeroPaddedWithPrefix_One")]
    [TestCase("5", false, true, "0x5", TestName = "U256Buffer_Trimmed_SingleNibble")]
    [TestCase("1234567", false, true, "0x1234567", TestName = "U256Buffer_Trimmed_OddNibbles")]
    [TestCase("ffffffffffffffff", false, true, "0xffffffffffffffff", TestName = "U256Buffer_Trimmed_UlongMax")]
    [TestCase("max", true, false, OnesWord, TestName = "U256Buffer_ZeroPaddedMaxValue_NoPrefix")]
    [TestCase("abcd", false, true, "0xabcd", TestName = "U256Buffer_Mid_TrimmedWithPrefix")]
    [TestCase("abcd", false, false, "abcd", TestName = "U256Buffer_Mid_TrimmedNoPrefix")]
    [TestCase("abcd", true, true, "0x" + "000000000000000000000000000000000000000000000000000000000000" + "abcd", TestName = "U256Buffer_Mid_ZeroPaddedWithPrefix")]
    [TestCase("abcd", true, false, "000000000000000000000000000000000000000000000000000000000000abcd", TestName = "U256Buffer_Mid_ZeroPaddedNoPrefix")]
    public void WriteUInt256HexString_AllVariants(string valueHex, bool zeroPadded, bool addPrefix, string expectedBody)
    {
        string actual = WriteToString(w => HexWriter.WriteUInt256HexString(w, UInt256FromHex(valueHex), zeroPadded, addPrefix));
        Assert.That(actual, Is.EqualTo("\"" + expectedBody + "\""));
    }

    [TestCase(0x0UL, true, true, "0x" + ZeroWord, TestName = "U256Prop_ZeroPaddedWithPrefix_Zero")]
    [TestCase(0x1UL, true, true, "0x" + "000000000000000000000000000000000000000000000000000000000000000" + "1", TestName = "U256Prop_ZeroPaddedWithPrefix_One")]
    [TestCase(0x1UL, false, true, "0x1", TestName = "U256Prop_TrimmedWithPrefix_One")]
    [TestCase(0x0UL, false, false, "0", TestName = "U256Prop_TrimmedNoPrefix_Zero")]
    [TestCase(0xabcdUL, false, false, "abcd", TestName = "U256Prop_TrimmedNoPrefix_Multi")]
    [TestCase(ulong.MaxValue, false, false, "ffffffffffffffff", TestName = "U256Prop_TrimmedNoPrefix_UlongMax")]
    [TestCase(0x1UL, true, false, "000000000000000000000000000000000000000000000000000000000000000" + "1", TestName = "U256Prop_ZeroPaddedNoPrefix_One")]
    public void WriteUInt256HexPropertyName_AllVariants(ulong value, bool zeroPadded, bool addPrefix, string expectedKey)
    {
        string actual = WriteToString(w =>
        {
            w.WriteStartObject();
            HexWriter.WriteUInt256HexPropertyName(w, (UInt256)value, zeroPadded, addPrefix);
            w.WriteNumberValue(1);
            w.WriteEndObject();
        });
        Assert.That(actual, Is.EqualTo("{\"" + expectedKey + "\":1}"));
    }
}
