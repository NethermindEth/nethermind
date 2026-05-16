// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using FluentAssertions;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class DoubleConverterTests
{
    private static readonly DoubleConverter _converter = new();

    private static readonly JsonSerializerOptions Options = new() { Converters = { _converter } };

    [TestCase(0.678584082336891, "0.678584082336891")]
    [TestCase(0.9985787551520126, "0.9985787551520126")]
    [TestCase(0.16666666666666666, "0.16666666666666666")]
    [TestCase(0.3333333333333333, "0.3333333333333333")]
    [TestCase(0.0105, "0.0105")]
    [TestCase(0.0, "0")]
    [TestCase(1.0, "1")]
    public void Write_PreservesFullIeee754Precision(double value, string expected)
    {
        string json = JsonSerializer.Serialize(value, Options);
        json.Should().Be(expected, "double serialization must preserve full IEEE 754 round-trip precision");
    }

    [TestCase(0.678584082336891)]
    [TestCase(0.9985787551520126)]
    [TestCase(0.16666666666666666)]
    [TestCase(0.3333333333333333)]
    public void Roundtrip_PreservesValue(double value)
    {
        string json = JsonSerializer.Serialize(value, Options);
        double deserialized = JsonSerializer.Deserialize<double>(json, Options);
        deserialized.Should().Be(value, "deserialization must recover the original double value exactly");
    }
}
