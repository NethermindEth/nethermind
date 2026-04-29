// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Clear-only latency: each invocation clears a fully populated cache.
/// <see cref="IterationSetupAttribute"/> refills all caches before each single-invocation iteration
/// so every measurement sees a full cache.
/// </summary>
[MemoryDiagnoser]
[InvocationCount(1)]
[WarmupCount(5)]
[MinIterationCount(50)]
[MaxIterationCount(100)]
public class CacheClearBenchmarks : CacheBenchmarkBase
{
    [Params(256, 4_096, 65_536)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup() => SetupCaches(KeyCount);

    [IterationSetup]
    public void FillCaches()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            _lru.Set(_keys[i], _accounts[i]);
            _clock.Set(_keys[i], _accounts[i]);
            _assoc.Set(in _keys[i], _accounts[i]);
        }
    }

    // ==================== Clear ====================

    [Benchmark(Baseline = true)]
    public void LruCache_Clear() => _lru.Clear();

    [Benchmark]
    public void ClockCache_Clear() => _clock.Clear();

    [Benchmark]
    public void AssociativeCache_Clear_ReleaseRefs() => _assoc.Clear(releaseReferences: true);

    [Benchmark]
    public void AssociativeCache_Clear_KeepRefs() => _assoc.Clear(releaseReferences: false);
}

/// <summary>
/// Fill-then-clear cycle: measures the total cost of populating and clearing each cache.
/// Represents real-world patterns like HashCache which fills during a block and clears between blocks.
/// No <see cref="IterationSetupAttribute"/> needed — each invocation fills and clears independently.
/// </summary>
[MemoryDiagnoser]
public class CacheFillAndClearCycleBenchmarks : CacheBenchmarkBase
{
    [Params(256, 4_096, 65_536)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup() => SetupCaches(KeyCount);

    // ==================== Fill + Clear ====================

    [Benchmark(Baseline = true)]
    public void LruCache_FillAndClear()
    {
        for (int i = 0; i < KeyCount; i++)
            _lru.Set(_keys[i], _accounts[i]);
        _lru.Clear();
    }

    [Benchmark]
    public void ClockCache_FillAndClear()
    {
        for (int i = 0; i < KeyCount; i++)
            _clock.Set(_keys[i], _accounts[i]);
        _clock.Clear();
    }

    [Benchmark]
    public void AssociativeCache_FillAndClear_ReleaseRefs()
    {
        for (int i = 0; i < KeyCount; i++)
            _assoc.Set(in _keys[i], _accounts[i]);
        _assoc.Clear(releaseReferences: true);
    }

    [Benchmark]
    public void AssociativeCache_FillAndClear_KeepRefs()
    {
        for (int i = 0; i < KeyCount; i++)
            _assoc.Set(in _keys[i], _accounts[i]);
        _assoc.Clear(releaseReferences: false);
    }
}
