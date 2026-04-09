// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Caching;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching;

/// <summary>
/// Measures actual eviction quality (hit rate %) for LruCache, ClockCache, and AssociativeCache
/// under different access patterns. Not a pass/fail test — outputs comparison tables.
/// </summary>
[TestFixture]
[Explicit("Eviction quality measurement; no assertions, not suitable as CI pass/fail signal.")]
public class CacheEvictionQualityTests
{
    private AddressAsKey[] _keys = null!;
    private Account[] _values = null!;

    private const int KeyPoolSize = 100_000;

    [OneTimeSetUp]
    public void Setup()
    {
        (_keys, _values) = CacheTestData.Build(KeyPoolSize);
    }

    [Test]
    [TestCase(256)]
    [TestCase(1024)]
    [TestCase(4096)]
    [TestCase(16384)]
    public void Uniform_random_workload(int capacity)
    {
        // Simulate uniform random access: keys drawn uniformly from [0, 2*capacity)
        // This tests how well each cache handles a working set larger than capacity.
        int keyRange = capacity * 2;
        int totalOps = capacity * 20;
        Random random = new(42);

        int[] accessPattern = new int[totalOps];
        for (int i = 0; i < totalOps; i++)
        {
            accessPattern[i] = random.Next(0, keyRange);
        }

        double lruHitRate = MeasureHitRate_Lru(capacity, accessPattern);
        double clockHitRate = MeasureHitRate_Clock(capacity, accessPattern);
        double assocHitRate = MeasureHitRate_Associative(capacity, accessPattern);

        TestContext.Out.WriteLine($"=== Uniform Random (capacity={capacity}, keyRange={keyRange}, ops={totalOps}) ===");
        TestContext.Out.WriteLine($"  LruCache:         {lruHitRate,6:F2}%");
        TestContext.Out.WriteLine($"  ClockCache:       {clockHitRate,6:F2}%");
        TestContext.Out.WriteLine($"  AssociativeCache: {assocHitRate,6:F2}%");
        TestContext.Out.WriteLine();
    }

    [Test]
    [TestCase(256)]
    [TestCase(1024)]
    [TestCase(4096)]
    [TestCase(16384)]
    public void Zipf_workload(int capacity)
    {
        // Zipf distribution: few keys are very hot, most are cold.
        // A good eviction policy should keep hot keys and evict cold ones.
        int keyRange = capacity * 4;
        int totalOps = capacity * 20;

        int[] accessPattern = GenerateZipfPattern(totalOps, keyRange, s: 1.0, seed: 42);

        double lruHitRate = MeasureHitRate_Lru(capacity, accessPattern);
        double clockHitRate = MeasureHitRate_Clock(capacity, accessPattern);
        double assocHitRate = MeasureHitRate_Associative(capacity, accessPattern);

        TestContext.Out.WriteLine($"=== Zipf s=1.0 (capacity={capacity}, keyRange={keyRange}, ops={totalOps}) ===");
        TestContext.Out.WriteLine($"  LruCache:         {lruHitRate,6:F2}%");
        TestContext.Out.WriteLine($"  ClockCache:       {clockHitRate,6:F2}%");
        TestContext.Out.WriteLine($"  AssociativeCache: {assocHitRate,6:F2}%");
        TestContext.Out.WriteLine();
    }

    [Test]
    [TestCase(256)]
    [TestCase(1024)]
    [TestCase(4096)]
    [TestCase(16384)]
    public void Working_set_with_scan(int capacity)
    {
        // Hot working set (50% of capacity) accessed 90% of the time,
        // with occasional sequential scans through cold keys.
        // LRU is vulnerable to scans evicting the working set; random-based policies are more resistant.
        int hotSetSize = capacity / 2;
        int coldKeyCount = Math.Min(capacity * 10, KeyPoolSize - hotSetSize - 1);
        int totalOps = capacity * 20;
        Random random = new(42);

        int[] accessPattern = new int[totalOps];
        for (int i = 0; i < totalOps; i++)
        {
            if (random.NextDouble() < 0.9)
            {
                // 90% hot working set
                accessPattern[i] = random.Next(0, hotSetSize);
            }
            else
            {
                // 10% sequential scan through cold keys
                accessPattern[i] = hotSetSize + (i % coldKeyCount);
            }
        }

        double lruHitRate = MeasureHitRate_Lru(capacity, accessPattern);
        double clockHitRate = MeasureHitRate_Clock(capacity, accessPattern);
        double assocHitRate = MeasureHitRate_Associative(capacity, accessPattern);

        TestContext.Out.WriteLine($"=== Working Set + Scan (capacity={capacity}, hotSet={hotSetSize}, coldKeys={coldKeyCount}) ===");
        TestContext.Out.WriteLine($"  LruCache:         {lruHitRate,6:F2}%");
        TestContext.Out.WriteLine($"  ClockCache:       {clockHitRate,6:F2}%");
        TestContext.Out.WriteLine($"  AssociativeCache: {assocHitRate,6:F2}%");
        TestContext.Out.WriteLine();
    }

    [Test]
    [TestCase(256)]
    [TestCase(1024)]
    [TestCase(4096)]
    [TestCase(16384)]
    public void Sequential_scan(int capacity)
    {
        // Worst case for LRU: pure sequential scan through keys 0..N
        // Every access is a miss once the scan exceeds capacity.
        // Random-based policies degrade more gracefully.
        int keyRange = capacity * 4;
        int totalOps = capacity * 20;

        int[] accessPattern = new int[totalOps];
        for (int i = 0; i < totalOps; i++)
        {
            accessPattern[i] = i % keyRange;
        }

        double lruHitRate = MeasureHitRate_Lru(capacity, accessPattern);
        double clockHitRate = MeasureHitRate_Clock(capacity, accessPattern);
        double assocHitRate = MeasureHitRate_Associative(capacity, accessPattern);

        TestContext.Out.WriteLine($"=== Sequential Scan (capacity={capacity}, keyRange={keyRange}, ops={totalOps}) ===");
        TestContext.Out.WriteLine($"  LruCache:         {lruHitRate,6:F2}%");
        TestContext.Out.WriteLine($"  ClockCache:       {clockHitRate,6:F2}%");
        TestContext.Out.WriteLine($"  AssociativeCache: {assocHitRate,6:F2}%");
        TestContext.Out.WriteLine();
    }

    private double MeasureHitRate_Lru(int capacity, int[] accessPattern)
    {
        LruCache<AddressAsKey, Account> cache = new(capacity, "test");
        int hits = 0;

        for (int i = 0; i < accessPattern.Length; i++)
        {
            int keyIdx = accessPattern[i];
            if (cache.Get(_keys[keyIdx]) is not null)
            {
                hits++;
            }
            else
            {
                cache.Set(_keys[keyIdx], _values[keyIdx]);
            }
        }

        return (double)hits / accessPattern.Length * 100.0;
    }

    private double MeasureHitRate_Clock(int capacity, int[] accessPattern)
    {
        ClockCache<AddressAsKey, Account> cache = new(capacity);
        int hits = 0;

        for (int i = 0; i < accessPattern.Length; i++)
        {
            int keyIdx = accessPattern[i];
            if (cache.TryGet(_keys[keyIdx], out _))
            {
                hits++;
            }
            else
            {
                cache.Set(_keys[keyIdx], _values[keyIdx]);
            }
        }

        return (double)hits / accessPattern.Length * 100.0;
    }

    private double MeasureHitRate_Associative(int capacity, int[] accessPattern)
    {
        AssociativeCache<AddressAsKey, Account> cache = new(capacity);
        int hits = 0;

        for (int i = 0; i < accessPattern.Length; i++)
        {
            int keyIdx = accessPattern[i];
            AddressAsKey key = _keys[keyIdx];
            if (cache.TryGet(in key, out _))
            {
                hits++;
            }
            else
            {
                cache.Set(in key, _values[keyIdx]);
            }
        }

        return (double)hits / accessPattern.Length * 100.0;
    }

    /// <summary>
    /// Generates a Zipf-distributed access pattern.
    /// Key i has probability proportional to 1/i^s.
    /// </summary>
    private static int[] GenerateZipfPattern(int count, int keyRange, double s, int seed)
    {
        Random random = new(seed);

        // Build CDF
        double[] cdf = new double[keyRange];
        double sum = 0;
        for (int i = 0; i < keyRange; i++)
        {
            sum += 1.0 / Math.Pow(i + 1, s);
            cdf[i] = sum;
        }

        // Normalize
        for (int i = 0; i < keyRange; i++)
        {
            cdf[i] /= sum;
        }

        // Sample
        int[] pattern = new int[count];
        for (int i = 0; i < count; i++)
        {
            double r = random.NextDouble();
            int idx = Array.BinarySearch(cdf, r);
            if (idx < 0) idx = ~idx;
            if (idx >= keyRange) idx = keyRange - 1;
            pattern[i] = idx;
        }

        return pattern;
    }
}
