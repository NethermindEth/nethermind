// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core.Caching;
using Perfolizer.Horology;

namespace CacheExperiments;

[MemoryDiagnoser]
[Config(typeof(Configuration))]
public class LruBenchmarks
{
    private const int Operations = 4096;
    private ICache<int, int> _cache = null!;
    private int[] _keys = null!;
    private int _next;

    [Params("original", "fastpath", "slots")]
    public string Variant { get; set; } = null!;

    [Params(1024, 16384)]
    public int Capacity { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cache = Variant switch
        {
            "original" => new Baseline.LruCache<int, int>(Capacity, Capacity, "bench"),
            "fastpath" => new FastPath.LruCache<int, int>(Capacity, Capacity, "bench"),
            _ => new LruCache<int, int>(Capacity, Capacity, "bench"),
        };
        _keys = new int[Operations];
        Random random = new(42);
        for (int i = 0; i < _keys.Length; i++) _keys[i] = random.Next(Capacity);
        for (int i = 0; i < Capacity; i++) _cache.Set(i, i);
        _next = Capacity;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public int HotHit()
    {
        int sum = 0;
        for (int i = 0; i < Operations; i++) sum += _cache.Get(Capacity - 1);
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public int UniformHit()
    {
        int sum = 0;
        for (int i = 0; i < Operations; i++) sum += _cache.Get(_keys[i]);
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public void Update()
    {
        for (int i = 0; i < Operations; i++) _cache.Set(_keys[i], i);
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public void Evict()
    {
        for (int i = 0; i < Operations; i++) _cache.Set(_next++, i);
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public void DeleteRefill()
    {
        for (int i = 0; i < Operations; i++)
        {
            _cache.Delete(_keys[i]);
            _cache.Set(_keys[i], i);
        }
    }

    [Benchmark(OperationsPerInvoke = 16384)]
    public void ClearRefill()
    {
        for (int i = 0; i < 16384; i++)
        {
            if (i % Capacity == 0) _cache.Clear();
            _cache.Set(i % Capacity, i);
        }
    }

    private sealed class Configuration : ManualConfig
    {
        public Configuration() => AddJob(Job.ShortRun
            .WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithIterationTime(TimeInterval.FromMilliseconds(100))
            .WithWarmupCount(3)
            .WithIterationCount(7));
    }
}
