// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Test;
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

    [Test]
    public void ShouldRunInChunk_WithoutTestChunkSet_ReturnsTrueForEverything()
    {
        Environment.SetEnvironmentVariable("TEST_CHUNK", null);

        Assert.That(TestChunkFilter.ShouldRunInChunk(""), Is.True);
        Assert.That(TestChunkFilter.ShouldRunInChunk("Some.Test.FullName"), Is.True);
        Assert.That(TestChunkFilter.ShouldRunInChunk("Another.Test"), Is.True);
    }

    [Test]
    public void ShouldRunInChunk_PartitionsTestsAcrossAllChunksWithoutGapsOrOverlap()
    {
        const int chunkTotal = 4;
        const int testCount = 1000;
        int[] runsPerChunk = new int[chunkTotal];

        for (int chunkIndex = 1; chunkIndex <= chunkTotal; chunkIndex++)
        {
            Environment.SetEnvironmentVariable("TEST_CHUNK", $"{chunkIndex}of{chunkTotal}");
            for (int testIndex = 0; testIndex < testCount; testIndex++)
            {
                if (TestChunkFilter.ShouldRunInChunk($"Nethermind.Tests.Fixture.Method({testIndex})"))
                {
                    runsPerChunk[chunkIndex - 1]++;
                }
            }
        }

        Assert.That(runsPerChunk.Sum(), Is.EqualTo(testCount), "every test must be claimed by exactly one chunk");
        foreach (int count in runsPerChunk)
        {
            Assert.That(count, Is.GreaterThan(testCount / chunkTotal / 2), "partitioning should not collapse all tests into one chunk");
        }
    }

    [Test]
    public void ShouldRunInChunk_IsStableAcrossInvocations()
    {
        Environment.SetEnvironmentVariable("TEST_CHUNK", "2of5");
        const string name = "Nethermind.Synchronization.Test.FastSync.SomeTest.Bar(arg=42)";

        bool first = TestChunkFilter.ShouldRunInChunk(name);
        for (int i = 0; i < 100; i++)
        {
            Assert.That(TestChunkFilter.ShouldRunInChunk(name), Is.EqualTo(first));
        }
    }

    [TestCase("0of4")]
    [TestCase("5of4")]
    [TestCase("-1of4")]
    [TestCase("1of0")]
    [TestCase("1of-1")]
    [TestCase("badformat")]
    [TestCase("1of")]
    [TestCase("of4")]
    public void TryGetChunkConfig_RejectsInvalidFormat(string invalid)
    {
        Environment.SetEnvironmentVariable("TEST_CHUNK", invalid);

        Assert.Throws<ArgumentException>(() => TestChunkFilter.TryGetChunkConfig());
    }

    [TestCase("1of1", 1, 1)]
    [TestCase("1of4", 1, 4)]
    [TestCase("4of4", 4, 4)]
    [TestCase("3of10", 3, 10)]
    public void TryGetChunkConfig_ParsesValidFormat(string env, int expectedIndex, int expectedTotal)
    {
        Environment.SetEnvironmentVariable("TEST_CHUNK", env);

        (int Index, int Total)? config = TestChunkFilter.TryGetChunkConfig();

        Assert.That(config, Is.Not.Null);
        Assert.That(config!.Value.Index, Is.EqualTo(expectedIndex));
        Assert.That(config.Value.Total, Is.EqualTo(expectedTotal));
    }

    [Test]
    public void TryGetChunkConfig_WhenUnset_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("TEST_CHUNK", null);

        Assert.That(TestChunkFilter.TryGetChunkConfig(), Is.Null);
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
