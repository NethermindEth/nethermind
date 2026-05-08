// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

[NonParallelizable]
public class TestChunkFilterTests
{
    private string? _testChunk;

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

    private static IEnumerable<int> CountedRange(int count, Action onEnumerated)
    {
        for (int i = 0; i < count; i++)
        {
            onEnumerated();
            yield return i;
        }
    }
}
