// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class Hash256ConverterTests
{
    static readonly Hash256Converter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [Test]
    public void Can_read_null()
    {
        Hash256? result = JsonSerializer.Deserialize<Hash256>("null", options);
        Assert.That(result, Is.EqualTo(null));
    }

    [TestCaseSource(nameof(WriteTestCases))]
    public void Writes_correct_hex(Hash256 hash, string expected)
    {
        string result = JsonSerializer.Serialize(hash, options);
        Assert.That(result, Is.EqualTo(expected));
    }

    static IEnumerable<TestCaseData> WriteTestCases =
    [
        new TestCaseData(new Hash256(new byte[32]), "\"0x0000000000000000000000000000000000000000000000000000000000000000\"")
            .SetName("All zeros"),
        new TestCaseData(new Hash256(CreateFilledBytes(32, 0xFF)), "\"0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"")
            .SetName("All 0xFF"),
        new TestCaseData(new Hash256(CreateSequentialBytes(32)), "\"0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f\"")
            .SetName("Sequential bytes"),
        new TestCaseData(Keccak.OfAnEmptyString, $"\"0x{Keccak.OfAnEmptyString.ToString(false)}\"")
            .SetName("Keccak of empty string"),
    ];

    [TestCaseSource(nameof(RoundtripTestCases))]
    public void Writes_roundtrip(Hash256 hash)
    {
        string json = JsonSerializer.Serialize(hash, options);
        Hash256? deserialized = JsonSerializer.Deserialize<Hash256>(json, options);
        Assert.That(deserialized, Is.EqualTo(hash));
    }

    static IEnumerable<TestCaseData> RoundtripTestCases =
    [
        new TestCaseData(Keccak.Compute("test data"u8))
            .SetName("Keccak of test data"),
        new TestCaseData(new Hash256(CreateNibblePatternBytes()))
            .SetName("Nibble pattern"),
    ];

    static byte[] CreateFilledBytes(int length, byte value)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, value);
        return bytes;
    }

    static byte[] CreateSequentialBytes(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)i;
        return bytes;
    }

    static byte[] CreateNibblePatternBytes()
    {
        // Ensure all hex chars 0-f appear correctly
        byte[] bytes = new byte[32];
        for (int i = 0; i < 16; i++)
        {
            bytes[i * 2] = (byte)((i << 4) | i);           // 0x00, 0x11, 0x22, ..., 0xff
            bytes[i * 2 + 1] = (byte)((i << 4) | (15 - i)); // 0x0f, 0x1e, 0x2d, ...
        }
        return bytes;
    }
}
