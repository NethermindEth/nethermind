// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class DoubleArrayConverterTests : ConverterTestBase<double[]>
{
    static readonly DoubleArrayConverter converter = new();

    [TestCaseSource(nameof(RoundtripTestCases))]
    public void Test_roundtrip(double[] value) => TestConverter(value, static (a, b) => a.AsSpan().SequenceEqual(b), converter);

    static IEnumerable<TestCaseData> RoundtripTestCases()
    {
        yield return new TestCaseData(new double[] { -0.5, 0.5, 1.0, 1.5, 2.0, 2.5 }).SetName("Mixed values");
        yield return new TestCaseData(new double[] { 1, 1, 1, 1 }).SetName("All ones");
        yield return new TestCaseData(new double[] { 0, 0, 0, 0 }).SetName("All zeros");
        yield return new TestCaseData(Array.Empty<double>()).SetName("Empty array");
    }
}
