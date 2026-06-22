// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.JsonConverters;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
[NonParallelizable]
public class NullableQuantityUInt256ConverterTests
{
    private static readonly NullableQuantityUInt256Converter _converter = new();
    private static readonly JsonSerializerOptions _options = new() { Converters = { _converter } };

    private bool _previousStrictMode;

    [SetUp]
    public void SetUp() => _previousStrictMode = JsonRpcQuantityFormat.StrictMode;

    [TearDown]
    public void TearDown() => JsonRpcQuantityFormat.StrictMode = _previousStrictMode;

    [TestCase("\"0xa00000\"", 10485760UL)]
    [TestCase("\"0x0\"", 0UL)]
    [TestCase("\"0x0000\"", 0UL)]
    [TestCase("\"0x1\"", 1UL)]
    public void Can_read_value_lenient_mode(string json, ulong expected)
    {
        JsonRpcQuantityFormat.StrictMode = false;
        UInt256? result = JsonSerializer.Deserialize<UInt256?>(json, _options);
        Assert.That(result, Is.EqualTo((UInt256)expected));
    }

    [Test]
    public void Accepts_null()
    {
        UInt256? result = JsonSerializer.Deserialize<UInt256?>("null", _options);
        Assert.That(result, Is.Null);
    }

    [TestCase("\"0x0b\"")]
    [TestCase("\"0x00\"")]
    [TestCase("\"0x0ff\"")]
    public void Strict_rejects_leading_zero(string json)
    {
        JsonRpcQuantityFormat.StrictMode = true;
        Assert.That(() => JsonSerializer.Deserialize<UInt256?>(json, _options), Throws.InstanceOf<FormatException>());
    }

    [TestCase("\"0x0\"", 0UL)]
    [TestCase("\"0xb\"", 11UL)]
    [TestCase("\"0xff\"", 255UL)]
    public void Strict_accepts_valid_quantity(string json, ulong expected)
    {
        JsonRpcQuantityFormat.StrictMode = true;
        UInt256? result = JsonSerializer.Deserialize<UInt256?>(json, _options);
        Assert.That(result, Is.EqualTo((UInt256)expected));
    }

    [TestCase("\"0x0000\"")]
    [TestCase("\"0x0b\"")]
    public void Lenient_mode_accepts_leading_zeros(string json)
    {
        JsonRpcQuantityFormat.StrictMode = false;
        Assert.That(() => JsonSerializer.Deserialize<UInt256?>(json, _options), Throws.Nothing);
    }
}
