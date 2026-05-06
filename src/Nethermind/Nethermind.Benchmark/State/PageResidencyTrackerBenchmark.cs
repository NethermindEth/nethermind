// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.State.Flat.Storage;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Microbenchmark for <see cref="PageResidencyTracker.TryTouch"/> — the hot path called on every
/// arena read/pin. Sweeps three workloads against a fixed-capacity tracker (64K slots, ~1 GiB
/// of 16 KiB pages or 256 MiB of 4 KiB pages):
///   - HitOnly: working set fits in capacity, every touch is a no-op slot match.
///   - MissOnly: working set 2× capacity, every touch evicts (worst-case dispatch path).
///   - Mixed: working set ≈ capacity, mix of hits and collision evictions.
/// The benchmark only measures TryTouch — eviction dispatch happens at the call site in
/// production, but here we drop the displaced key on the floor so we measure the tracker itself,
/// not <c>madvise</c>.
/// </summary>
[MemoryDiagnoser]
public class PageResidencyTrackerBenchmark
{
    public enum Workload
    {
        HitOnly,
        MissOnly,
        Mixed,
    }

    private const int BatchSize = 16_384;

    private PageResidencyTracker _tracker = null!;
    private int[] _arenaIds = null!;
    private int[] _pageIdxs = null!;

    [Params(65_536)]
    public int Capacity { get; set; }

    [Params(Workload.HitOnly, Workload.MissOnly, Workload.Mixed)]
    public Workload Pattern { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _tracker = new PageResidencyTracker(Capacity);

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
            _tracker.TryTouch(_arenaIds[i], _pageIdxs[i], out _, out _);
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public int Touch()
    {
        int[] arenas = _arenaIds;
        int[] pages = _pageIdxs;
        PageResidencyTracker tracker = _tracker;
        int evicted = 0;
        for (int i = 0; i < BatchSize; i++)
        {
            if (tracker.TryTouch(arenas[i], pages[i], out _, out _)) evicted++;
        }
        return evicted;
    }
}
