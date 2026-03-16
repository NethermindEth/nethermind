// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;

using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class NullableBigIntegerConverterTests : ConverterTestBase<BigInteger?>
{
    static readonly NullableBigIntegerConverter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [TestCaseSource(nameof(RoundtripTestCases))]
    public void Test_roundtrip(BigInteger? value)
    {
        TestConverter(value, static (a, b) => a.Equals(b), converter);
    }

    static IEnumerable<TestCaseData> RoundtripTestCases =
    [
        new TestCaseData(null).SetName("null"),
        new TestCaseData((BigInteger?)int.MaxValue).SetName("intMaxValue"),
        new TestCaseData((BigInteger?)BigInteger.One).SetName("one"),
        new TestCaseData((BigInteger?)BigInteger.Zero).SetName("zero"),
    ];

    [TestCase("0", "0")]
    [TestCase("1", "1")]
    [TestCase("null", null)]
    public void Can_read_value(string json, string? expected)
    {
        BigInteger? result = JsonSerializer.Deserialize<BigInteger?>(json, options);
        Assert.That(result, Is.EqualTo(expected is null ? null : BigInteger.Parse(expected)));
    }
}
