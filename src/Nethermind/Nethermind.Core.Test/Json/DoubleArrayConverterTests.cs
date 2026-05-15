// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class DoubleArrayConverterTests : ConverterTestBase<double[]>
{
    private static readonly DoubleArrayConverter _converter = new();

    [TestCaseSource(nameof(RoundtripTestCases))]
    public void Test_roundtrip(double[] value) => TestConverter(value, static (a, b) => a.AsSpan().SequenceEqual(b), _converter);

    static IEnumerable<TestCaseData> RoundtripTestCases()
    {
        yield return new TestCaseData(new double[] { -0.5, 0.5, 1.0, 1.5, 2.0, 2.5 }).SetName("Mixed values");
        yield return new TestCaseData(new double[] { 1, 1, 1, 1 }).SetName("All ones");
        yield return new TestCaseData(new double[] { 0, 0, 0, 0 }).SetName("All zeros");
        yield return new TestCaseData(Array.Empty<double>()).SetName("Empty array");
        yield return new TestCaseData(new double[] { 1.0 / 3.0, 1.0 / 6.0, 0.678584082336891, 0.9985787551520126 }).SetName("Full precision values");
    }

    [TestCase(0.678584082336891, "[0.678584082336891]")]
    [TestCase(0.9985787551520126, "[0.9985787551520126]")]
    [TestCase(0.16666666666666666, "[0.16666666666666666]")]
    [TestCase(0.3333333333333333, "[0.3333333333333333]")]
    public void Write_PreservesFullIeee754Precision(double value, string expectedJson)
    {
        JsonSerializerOptions options = new() { Converters = { _converter } };
        string json = JsonSerializer.Serialize(new[] { value }, options);
        json.Should().Be(expectedJson, "double array serialization must preserve full IEEE 754 round-trip precision");
    }
}
