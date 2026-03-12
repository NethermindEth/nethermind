// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class BloomConverterTests : ConverterTestBase<Bloom>
{
    static readonly BloomConverter converter = new();

    [TestCaseSource(nameof(BloomTestCases))]
    public void Test_roundtrip(Bloom? value)
    {
        TestConverter(value!, static (a, b) => a is null ? b is null : a.Equals(b), converter);
    }

    static object?[] BloomTestCases =
    [
        new object?[] { null },
        new object?[] { Bloom.Empty },
        new object?[] { new Bloom(Enumerable.Range(0, 255).Select(static i => (byte)i).ToArray()) },
    ];
}
