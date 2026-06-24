// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.JsonConverters;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class NullableQuantityULongConverterTests
{
    private static readonly NullableQuantityULongConverter _converter = new();
    private static readonly JsonSerializerOptions _options = new() { Converters = { _converter } };

    [NonParallelizable]
    [TestCase("\"0xa00000\"", 10485760UL)]
    [TestCase("\"0x0\"", 0UL)]
    [TestCase("\"0x0000\"", 0UL)]
    [TestCase("\"0x1\"", 1UL)]
    public void Can_read_value_lenient_mode(string json, ulong expected)
    {
        bool previous = JsonRpcQuantityFormat.StrictMode;
        try
        {
            JsonRpcQuantityFormat.StrictMode = false;
            ulong? result = JsonSerializer.Deserialize<ulong?>(json, _options);
            Assert.That(result, Is.EqualTo(expected));
        }
        finally
        {
            JsonRpcQuantityFormat.StrictMode = previous;
        }
    }

    [Test]
    public void Accepts_null()
    {
        ulong? result = JsonSerializer.Deserialize<ulong?>("null", _options);
        Assert.That(result, Is.Null);
    }

    [NonParallelizable]
    [TestCase("\"0x0b\"")]
    [TestCase("\"0x00\"")]
    [TestCase("\"0x0ff\"")]
    public void Strict_rejects_leading_zero(string json)
    {
        bool previous = JsonRpcQuantityFormat.StrictMode;
        try
        {
            JsonRpcQuantityFormat.StrictMode = true;
            Assert.That(() => JsonSerializer.Deserialize<ulong?>(json, _options), Throws.InstanceOf<FormatException>());
        }
        finally
        {
            JsonRpcQuantityFormat.StrictMode = previous;
        }
    }

    [NonParallelizable]
    [TestCase("\"0x0\"", 0UL)]
    [TestCase("\"0xb\"", 11UL)]
    [TestCase("\"0xff\"", 255UL)]
    public void Strict_accepts_valid_quantity(string json, ulong expected)
    {
        bool previous = JsonRpcQuantityFormat.StrictMode;
        try
        {
            JsonRpcQuantityFormat.StrictMode = true;
            ulong? result = JsonSerializer.Deserialize<ulong?>(json, _options);
            Assert.That(result, Is.EqualTo(expected));
        }
        finally
        {
            JsonRpcQuantityFormat.StrictMode = previous;
        }
    }

    [NonParallelizable]
    [TestCase("\"0x0000\"")]
    [TestCase("\"0x0b\"")]
    public void Lenient_mode_accepts_leading_zeros(string json)
    {
        bool previous = JsonRpcQuantityFormat.StrictMode;
        try
        {
            JsonRpcQuantityFormat.StrictMode = false;
            Assert.That(() => JsonSerializer.Deserialize<ulong?>(json, _options), Throws.Nothing);
        }
        finally
        {
            JsonRpcQuantityFormat.StrictMode = previous;
        }
    }

    [TestCase(0UL, "\"0x0\"")]
    [TestCase(1UL, "\"0x1\"")]
    [TestCase(11UL, "\"0xb\"")]
    [TestCase(255UL, "\"0xff\"")]
    [TestCase(256UL, "\"0x100\"")]
    [TestCase(10485760UL, "\"0xa00000\"")]
    [TestCase(ulong.MaxValue, "\"0xffffffffffffffff\"")]
    public void Write_produces_minimal_quantity_hex(ulong input, string expectedJson)
    {
        string result = JsonSerializer.Serialize((ulong?)input, _options);
        Assert.That(result, Is.EqualTo(expectedJson));
    }

    [Test]
    public void Write_null_produces_null()
    {
        string result = JsonSerializer.Serialize((ulong?)null, _options);
        Assert.That(result, Is.EqualTo("null"));
    }
}
