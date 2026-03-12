// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class NullableLongConverterTests : ConverterTestBase<long?>
{
    static readonly NullableLongConverter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [TestCase(int.MaxValue)]
    [TestCase(1L)]
    [TestCase(0L)]
    public void Test_roundtrip(long value)
    {
        TestConverter((long?)value, static (a, b) => a.Equals(b), converter);
    }

    [TestCase("\"0xa00000\"", 10485760L)]
    [TestCase("\"0x0\"", 0L)]
    [TestCase("\"0x0000\"", 0L)]
    [TestCase("0", 0L)]
    [TestCase("1", 1L)]
    [TestCase("-1", -1L)]
    public void Can_read_value(string json, long expected)
    {
        long? result = JsonSerializer.Deserialize<long?>(json, options);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Can_read_null()
    {
        long? result = JsonSerializer.Deserialize<long?>("null", options);
        Assert.That(result, Is.EqualTo(null));
    }
}
