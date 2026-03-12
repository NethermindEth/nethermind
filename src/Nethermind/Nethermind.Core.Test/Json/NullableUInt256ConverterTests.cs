// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

using Nethermind.Int256;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class NullableUInt256ConverterTests : ConverterTestBase<UInt256?>
{
    static readonly NullableUInt256Converter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [TestCaseSource(nameof(RoundtripTestCases))]
    public void Test_roundtrip(UInt256? value)
    {
        TestConverter(value, static (a, b) => a.Equals(b), converter);
    }

    static object?[] RoundtripTestCases =
    [
        new object?[] { null },
        new object?[] { (UInt256?)int.MaxValue },
        new object?[] { (UInt256?)UInt256.One },
        new object?[] { (UInt256?)UInt256.Zero },
    ];

    [TestCase("\"0xa00000\"", "10485760")]
    [TestCase("\"0x0\"", "0")]
    [TestCase("0", "0")]
    [TestCase("1", "1")]
    public void Can_read_value(string json, string expected)
    {
        UInt256? result = JsonSerializer.Deserialize<UInt256?>(json, options);
        Assert.That(result, Is.EqualTo(UInt256.Parse(expected)));
    }
}
