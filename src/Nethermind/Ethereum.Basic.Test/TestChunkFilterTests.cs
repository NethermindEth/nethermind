// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

[NonParallelizable]
public class TestChunkFilterTests
{
    private string _testChunk;

    [SetUp]
    public void SetUp() =>
        _testChunk = Environment.GetEnvironmentVariable("TEST_CHUNK");

    [TearDown]
    public void TearDown() =>
        Environment.SetEnvironmentVariable("TEST_CHUNK", _testChunk);

    [Test]
    public void FilterByChunk_IsLazy()
    {
        Environment.SetEnvironmentVariable("TEST_CHUNK", "2of3");
        int enumeratedCount = 0;

        IEnumerable<int> filtered = TestChunkFilter.FilterByChunk(CountedRange(9, () => enumeratedCount++));

        Assert.That(enumeratedCount, Is.Zero);

        List<int> result = [];
        foreach (int value in filtered)
        {
            result.Add(value);
        }

        Assert.That(result, Is.EqualTo(new[] { 1, 4, 7 }));
        Assert.That(enumeratedCount, Is.EqualTo(9));
    }

    [TestCase("1of3", new[] { 0, 3, 6 })]
    [TestCase("2of3", new[] { 1, 4, 7 })]
    [TestCase("3of3", new[] { 2, 5, 8 })]
    [TestCase(null, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })]
    [TestCase("", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void FilterByChunk_WithExplicitChunk_IgnoresEnvironment(string chunk, int[] expected)
    {
        Environment.SetEnvironmentVariable("TEST_CHUNK", "1of9");

        Assert.That(TestChunkFilter.FilterByChunk(Enumerable.Range(0, 9), chunk), Is.EqualTo(expected));
    }

    [TestCase("8of3")]
    [TestCase("0of3")]
    [TestCase("garbage")]
    public void FilterByChunk_WithInvalidExplicitChunk_Throws(string chunk) =>
        Assert.Throws<ArgumentException>(() => TestChunkFilter.FilterByChunk(Enumerable.Range(0, 9), chunk));

    private static IEnumerable<int> CountedRange(int count, Action onEnumerated)
    {
        for (int i = 0; i < count; i++)
        {
            onEnumerated();
            yield return i;
        }
    }
}
