// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CacheExperiments;

internal static class Program
{
    private const int Operations = 262144;
    private static long _sink;

    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "validate")
        {
            ValidateClockVariants();
            return;
        }
        if (args.Length > 0 && args[0] == "bdn")
        {
            BenchmarkDotNet.Running.BenchmarkRunner.Run<LruBenchmarks>();
            return;
        }
        using StreamWriter output = new(args.Length == 0 ? "results.csv" : args[0]);
        output.WriteLine("variant,capacity,workload,ns_per_operation,bytes_per_operation,hit_percent");
        foreach (int capacity in new[] { 32, 1009, 1024, 16384 })
        {
            int raw = Math.Clamp(capacity / 64, 8, 256);
            int coprime = raw;
            while (Gcd(coprime, capacity) != 1) coprime++;
            int prime = raw;
            while (!IsPrime(prime) || Gcd(prime, capacity) != 1) prime++;
            (string Name, Func<Cache> Create)[] variants =
            [
                ("LRU-original", () => new OriginalLru(capacity)),
                ("LRU-MRU-fastpath", () => new FastLru(capacity)),
                ("LRU-slots", () => new SlotsLru(capacity)),
                ("CLOCK-original", () => new OriginalClock(capacity)),
                ("CLOCK-fixed32", () => new BoundedClock(capacity, 32)),
                ($"CLOCK-size-{raw}", () => new BoundedClock(capacity, raw)),
                ($"CLOCK-coprime-{coprime}", () => new BoundedClock(capacity, coprime)),
                ($"CLOCK-prime-{prime}", () => new BoundedClock(capacity, prime)),
            ];
            foreach (string workload in new[] { "hot-hit", "uniform-hit", "update", "evict", "delete-refill", "clear-refill", "hot-scan", "near-capacity", "phase-shift", "marked-scan" })
            {
                foreach ((string name, Func<Cache> create) in variants)
                {
                    int[] trace = Trace(workload, capacity);
                    Cache cache = create();
                    for (int i = 0; i < capacity; i++) cache.Set(i, i + 1);
                    for (int i = 0; i < 3; i++) Run(cache, workload, capacity, trace);
                    double[] times = new double[7];
                    double[] allocations = new double[7];
                    double hitRate = 0;
                    for (int repetition = 0; repetition < times.Length; repetition++)
                    {
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        long start = Stopwatch.GetTimestamp();
                        long hits = Run(cache, workload, capacity, trace);
                        long elapsed = Stopwatch.GetTimestamp() - start;
                        allocations[repetition] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)Operations;
                        times[repetition] = elapsed * 1e9 / Stopwatch.Frequency / Operations;
                        hitRate = hits * 100.0 / Operations;
                        _sink += hits;
                    }
                    Array.Sort(times);
                    Array.Sort(allocations);
                    string row = $"{name},{capacity},{workload},{times[3]:F2},{allocations[3]:F2},{hitRate:F3}";
                    output.WriteLine(row);
                    output.Flush();
                    Console.WriteLine(row);
                }
            }
        }
        Console.WriteLine($"Checksum: {_sink}");
    }

    private static void ValidateClockVariants()
    {
        int cases = 0;
        foreach (int capacity in new[] { 1, 3, 32, 1009, 1024 })
        {
            foreach (int limit in new[] { 1, 8, 17, 32, 256, capacity })
            {
                Bounded.ClockCache<int, int> cache = new(capacity, limit);
                for (int i = 0; i < capacity; i++) cache.Set(i, i + 1);
                for (int i = 0; i < capacity; i++) cache.Get(i);
                cache.Set(capacity, capacity + 1);
                int victim = Math.Min(limit, capacity) - 1;
                if (cache.Contains(victim) || cache.Count != capacity || cache.Get(capacity) != capacity + 1)
                    throw new InvalidOperationException($"Bounded eviction failed: {capacity}, {limit}");

                Parallel.For(0, 4, worker =>
                {
                    Random random = new(42 + worker);
                    for (int i = 0; i < 10000; i++)
                    {
                        int key = random.Next(capacity * 3);
                        switch (i % 4)
                        {
                            case 0: cache.Set(key, key + 1); break;
                            case 1: cache.Delete(key); break;
                            case 2:
                                if (cache.TryGet(key, out int value) && value != key + 1)
                                    throw new InvalidOperationException("Mismatched key and value");
                                break;
                            case 3:
                                if (i % 127 == 0) cache.Clear();
                                break;
                        }
                    }
                });
                if (cache.Count < 0 || cache.Count > capacity)
                    throw new InvalidOperationException("Invalid capacity after concurrent operations");
                cases++;
            }
        }
        Console.WriteLine($"Passed {cases} bounded CLOCK cases, including concurrent set/get/delete/clear.");
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    private static bool IsPrime(int number)
    {
        for (int divisor = 2; divisor <= number / divisor; divisor++)
            if (number % divisor == 0) return false;
        return number >= 2;
    }

    private static int[] Trace(string workload, int capacity)
    {
        Random random = new(42);
        int[] keys = new int[Operations];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = workload switch
            {
                "hot-hit" => capacity - 1,
                "evict" => capacity + i,
                "hot-scan" => i % 5 == 0 ? capacity + i : random.Next(Math.Max(1, capacity / 8)),
                "near-capacity" => i % 10 == 0 ? capacity + i : random.Next(Math.Max(1, capacity * 9 / 10)),
                "phase-shift" => ((i / 4096) % 4) * capacity + random.Next(Math.Max(1, capacity / 2)),
                _ => random.Next(capacity),
            };
        }
        return keys;
    }

    private static long Run(Cache cache, string workload, int capacity, int[] keys)
    {
        long hits = 0;
        if (workload == "marked-scan")
        {
            // Diagnostic moving-window sweep: misses are not reloaded, exposing displacement by bounded scans.
            cache.Clear();
            for (int j = 0; j < capacity; j++) cache.Set(j, j + 1);
            int next = capacity;
            for (int i = 0; i < Operations; i++)
            {
                int offset = i % (capacity + 1);
                if (offset == capacity) cache.Set(next, ++next);
                else hits += cache.Get(next - capacity + offset) != 0 ? 1 : 0;
            }
            return hits;
        }
        for (int i = 0; i < keys.Length; i++)
        {
            int key = keys[i];
            switch (workload)
            {
                case "update":
                case "evict":
                    cache.Set(key, i + 1);
                    break;
                case "delete-refill":
                    cache.Delete(key);
                    cache.Set(key, i + 1);
                    break;
                case "clear-refill":
                    if (i % capacity == 0) cache.Clear();
                    cache.Set(i % capacity, i + 1);
                    break;
                default:
                    if (cache.Get(key) != 0) hits++;
                    else cache.Set(key, key + 1);
                    break;
            }
        }
        return hits;
    }

    private abstract class Cache
    {
        public abstract int Get(int key);
        public abstract void Set(int key, int value);
        public abstract void Delete(int key);
        public abstract void Clear();
    }

    private sealed class OriginalLru(int capacity) : Cache
    {
        private readonly Baseline.LruCache<int, int> _cache = new(capacity, capacity, "bench");
        public override int Get(int key) => _cache.Get(key);
        public override void Set(int key, int value) => _cache.Set(key, value);
        public override void Delete(int key) => _cache.Delete(key);
        public override void Clear() => _cache.Clear();
    }

    private sealed class FastLru(int capacity) : Cache
    {
        private readonly FastPath.LruCache<int, int> _cache = new(capacity, capacity, "bench");
        public override int Get(int key) => _cache.Get(key);
        public override void Set(int key, int value) => _cache.Set(key, value);
        public override void Delete(int key) => _cache.Delete(key);
        public override void Clear() => _cache.Clear();
    }

    private sealed class SlotsLru(int capacity) : Cache
    {
        private readonly Nethermind.Core.Caching.LruCache<int, int> _cache = new(capacity, capacity, "bench");
        public override int Get(int key) => _cache.Get(key);
        public override void Set(int key, int value) => _cache.Set(key, value);
        public override void Delete(int key) => _cache.Delete(key);
        public override void Clear() => _cache.Clear();
    }

    private sealed class OriginalClock(int capacity) : Cache
    {
        private readonly Baseline.ClockCache<int, int> _cache = new(capacity);
        public override int Get(int key) => _cache.Get(key);
        public override void Set(int key, int value) => _cache.Set(key, value);
        public override void Delete(int key) => _cache.Delete(key);
        public override void Clear() => _cache.Clear();
    }

    private sealed class BoundedClock(int capacity, int limit) : Cache
    {
        private readonly Bounded.ClockCache<int, int> _cache = new(capacity, limit);
        public override int Get(int key) => _cache.Get(key);
        public override void Set(int key, int value) => _cache.Set(key, value);
        public override void Delete(int key) => _cache.Delete(key);
        public override void Clear() => _cache.Clear();
    }
}
