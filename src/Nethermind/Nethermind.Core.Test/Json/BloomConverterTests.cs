// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class BloomConverterTests : ConverterTestBase<Bloom>
{
    static readonly BloomConverter converter = new();

    [TestCaseSource(nameof(BloomTestCases))]
    public void Test_roundtrip(Bloom? value) => TestConverter(value!, static (a, b) => a is null ? b is null : a.Equals(b), converter);

    [TestCase("\"0xabc\"", TestName = "3 hex digits (odd)")]
    public void Rejects_odd_length_hex(string json)
    {
        JsonSerializerOptions options = new() { Converters = { converter } };
        Assert.That(() => JsonSerializer.Deserialize<Bloom>(json, options), Throws.InstanceOf<FormatException>());
    }

    static IEnumerable<TestCaseData> BloomTestCases =
    [
        new TestCaseData(null).SetName("null"),
        new TestCaseData(Bloom.Empty).SetName("empty"),
        new TestCaseData(new Bloom(Enumerable.Range(0, 255).Select(static i => (byte)i).ToArray())).SetName("range_0_to_254"),
    ];
}
