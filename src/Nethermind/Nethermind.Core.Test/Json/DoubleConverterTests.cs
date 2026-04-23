// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class DoubleConverterTests
{
    static readonly DoubleConverter _converter = new();

    static JsonSerializerOptions Options => new() { Converters = { _converter } };

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
        Assert.That(json, Is.EqualTo(expected));
    }

    [TestCase(0.678584082336891)]
    [TestCase(0.9985787551520126)]
    [TestCase(0.16666666666666666)]
    [TestCase(0.3333333333333333)]
    public void Roundtrip_PreservesValue(double value)
    {
        string json = JsonSerializer.Serialize(value, Options);
        double deserialized = JsonSerializer.Deserialize<double>(json, Options);
        Assert.That(deserialized, Is.EqualTo(value));
    }
}
