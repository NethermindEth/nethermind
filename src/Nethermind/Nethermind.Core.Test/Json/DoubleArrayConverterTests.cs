// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class DoubleArrayConverterTests : ConverterTestBase<double[]>
{
    static readonly DoubleArrayConverter converter = new();

    [Test]
    public void Test_roundtrip()
    {
        TestConverter(new double[] { -0.5, 0.5, 1.0, 1.5, 2.0, 2.5 }, (a, b) => a.AsSpan().SequenceEqual(b), converter);
        TestConverter(new double[] { 1, 1, 1, 1 }, (a, b) => a.AsSpan().SequenceEqual(b), converter);
        TestConverter(new double[] { 0, 0, 0, 0 }, (a, b) => a.AsSpan().SequenceEqual(b), converter);
        TestConverter(Array.Empty<double>(), (a, b) => a.AsSpan().SequenceEqual(b), converter);
    }
}
