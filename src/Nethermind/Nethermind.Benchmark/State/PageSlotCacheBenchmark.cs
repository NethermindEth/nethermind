// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.State.Flat.Storage;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Microbenchmark for <see cref="PageSlotCache"/>.<see cref="PageSlotCache.Touch"/> — the hot
/// path called on every arena read/pin. Sweeps three workloads against a fixed-capacity cache
/// (64K slots, ~1 GiB of 16 KiB pages or 256 MiB of 4 KiB pages):
///   - HitOnly: working set fits in capacity, every touch is a no-op slot match.
///   - MissOnly: working set 2× capacity, every touch evicts (worst-case eviction-handler call).
///   - Mixed: working set ≈ capacity, mix of hits and collision evictions.
/// The eviction handler is a no-op so we measure the cache itself, not <c>madvise</c>.
/// </summary>
[MemoryDiagnoser]
public class PageSlotCacheBenchmark
{
    public enum Workload
    {
        HitOnly,
        MissOnly,
        Mixed,
    }

    private sealed class NoopHandler : IPageEvictionHandler
    {
        public static readonly NoopHandler Instance = new();
        public void OnPageEvicted(int arenaId, int pageIdx) { }
    }

    private const int BatchSize = 16_384;

    private PageSlotCache _cache = null!;
    private int[] _arenaIds = null!;
    private int[] _pageIdxs = null!;

    [Params(65_536)]
    public int Capacity { get; set; }

    [Params(Workload.HitOnly, Workload.MissOnly, Workload.Mixed)]
    public Workload Pattern { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cache = new PageSlotCache(Capacity, NoopHandler.Instance);

        int workingSet = Pattern switch
        {
            Workload.HitOnly => Capacity / 2,
            Workload.MissOnly => Capacity * 2,
            Workload.Mixed => Capacity,
            _ => Capacity,
        };

        Random rng = new(42);
        _arenaIds = new int[BatchSize];
        _pageIdxs = new int[BatchSize];
        for (int i = 0; i < BatchSize; i++)
        {
            int id = rng.Next(workingSet);
            // Spread across a few arenas so the hash isn't dominated by pageIdx alone.
            _arenaIds[i] = id & 0x7;
            _pageIdxs[i] = id >> 3;
        }

        // Pre-warm: insert the working-set so HitOnly is actually hits and MissOnly steady-state.
        for (int i = 0; i < BatchSize; i++)
            _cache.Touch(_arenaIds[i], _pageIdxs[i]);
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public int Touch()
    {
        int[] arenas = _arenaIds;
        int[] pages = _pageIdxs;
        PageSlotCache cache = _cache;
        for (int i = 0; i < BatchSize; i++)
            cache.Touch(arenas[i], pages[i]);
        return BatchSize;
    }
}
