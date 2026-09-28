// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;

using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class BigIntegerConverterTests : ConverterTestBase<BigInteger>
{
    static readonly BigIntegerConverter converter = new();
    static readonly JsonSerializerOptions options = new() { Converters = { converter } };

    [TestCaseSource(nameof(RoundtripTestCases))]
    public void Test_roundtrip(BigInteger value) => TestConverter(value, static (a, b) => a.Equals(b), converter);

    // The int.MaxValue roundtrip lives in Serializes_as_decimal_string.
    static IEnumerable<TestCaseData> RoundtripTestCases =
    [
        new TestCaseData(BigInteger.One)
            .SetName("One"),
        new TestCaseData(BigInteger.Zero)
            .SetName("Zero"),
    ];

    [TestCase("0", "0")]
    [TestCase("1", "1")]
    public void Can_read_number(string json, string expected)
    {
        BigInteger result = JsonSerializer.Deserialize<BigInteger>(json, options);
        Assert.That(result, Is.EqualTo(BigInteger.Parse(expected)));
    }

    [Test]
    public void Serializes_as_decimal_string() =>
        TestConverter((BigInteger)int.MaxValue, "\"2147483647\"", converter, static (a, b) => a.Equals(b));
}
