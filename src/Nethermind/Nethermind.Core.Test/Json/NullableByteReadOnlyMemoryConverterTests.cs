// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class NullableByteReadOnlyMemoryConverterTests : ConverterTestBase<ReadOnlyMemory<byte>?>
{
    static readonly NullableByteReadOnlyMemoryConverter ConverterWithLeadingZeros = new(false);
    static readonly NullableByteReadOnlyMemoryConverter ConverterWithoutLeadingZeros = new(true);
    static readonly JsonSerializerOptions options = new() { Converters = { ConverterWithoutLeadingZeros } };

    [TestCaseSource(nameof(NullableBytesTestCaseSource))]
    public void Test_roundtrip_with_leading_zeros(ReadOnlyMemory<byte>? value, string? _)
        => TestConverter(value, static (src, expected) => (src is null && expected is null) || src is not null && expected is not null && src.Value.Span.SequenceEqual(expected.Value.Span), ConverterWithLeadingZeros);


    [TestCaseSource(nameof(NullableBytesTestCaseSource))]
    public void Test_serialization_without_leading_zeros(ReadOnlyMemory<byte>? value, string? expectedSerialization)
    {
        string result = JsonSerializer.Serialize(value, options);
        Assert.That(result, Is.EqualTo(expectedSerialization));
    }

    public static IEnumerable<TestCaseData> NullableBytesTestCaseSource
    {
        get
        {
            yield return new TestCaseData(null, "null") { TestName = "Null maps into null" };
            yield return new TestCaseData(ReadOnlyMemory<byte>.Empty, "\"0x\"") { TestName = "Empty array maps into 0x" };
            yield return new TestCaseData(new ReadOnlyMemory<byte>([0]), "\"0x0\"") { TestName = "0 maps into 0x0" };
            yield return new TestCaseData(new ReadOnlyMemory<byte>([0, 0, 0, 0]), "\"0x0\"") { TestName = "Any zeros map into 0x0" };
            yield return new TestCaseData(new ReadOnlyMemory<byte>([1]), "\"0x1\"") { TestName = "Number maps into a hex number" };
            yield return new TestCaseData(new ReadOnlyMemory<byte>([0, 1]), "\"0x1\"") { TestName = "Number maps into a hex number" };
            yield return new TestCaseData(new ReadOnlyMemory<byte>([0, 0, 0, 1]), "\"0x1\"") { TestName = "Number maps into a hex number" };
        }
    }
}
