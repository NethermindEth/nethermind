// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Pointer-chasing latency benchmark across the cache hierarchy. Allocates a
/// working set of <see cref="WorkingSetBytes"/> long-aligned slots, links them
/// into one Hamiltonian cycle of random next-pointers, then walks the cycle
/// serially. Each iteration is one dependent load, so the reported time per
/// chase is the average random-access latency at that working-set size.
///
/// Stride is held to one cache line (64 B) so the prefetcher can't see the
/// access pattern and ranges with no actual reuse don't get counted twice.
///
/// Recommended invocation: <c>--filter '*MemoryLatencyBenchmarks*'
/// --launchCount 1 --warmupCount 3 --iterationCount 5</c>.
/// </summary>
public class MemoryLatencyBenchmarks
{
    private const int LineBytes = 64;
    private const int ChasesPerInvocation = 1_000_000;

    private long[] _next = null!;
    private int _start;

    [Params(
        4 * 1024,         // L1 (~32 KB on most CPUs; 4K stays well inside)
        32 * 1024,        // L1 boundary
        256 * 1024,       // L2
        2 * 1024 * 1024,  // L2 boundary
        32 * 1024 * 1024, // L3
        256 * 1024 * 1024 // DRAM
    )]
    public int WorkingSetBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        int slotCount = WorkingSetBytes / LineBytes;
        // We hold an indirect-index per slot stored as a long; the array itself
        // is slotCount longs, but we only touch one long per cache line so the
        // backing memory consumed is slotCount * 8 bytes — comfortably inside
        // the requested working set.
        _next = new long[slotCount * (LineBytes / sizeof(long))];

        // Build a random cyclic permutation over [0, slotCount).
        int[] perm = new int[slotCount];
        for (int i = 0; i < slotCount; i++) perm[i] = i;
        Random rng = new(0xC0FFEE);
        for (int i = slotCount - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        // perm defines a cycle: perm[0] -> perm[1] -> ... -> perm[n-1] -> perm[0].
        // Store next slot's flat index (in longs) at the head-of-line word of the
        // current slot.
        int stride = LineBytes / sizeof(long);
        for (int i = 0; i < slotCount; i++)
        {
            int from = perm[i] * stride;
            int to = perm[(i + 1) % slotCount] * stride;
            _next[from] = to;
        }
        _start = perm[0] * stride;
    }

    [Benchmark(OperationsPerInvoke = ChasesPerInvocation)]
    public long Chase()
    {
        long[] arr = _next;
        long p = _start;
        for (int i = 0; i < ChasesPerInvocation; i++)
            p = arr[p];
        return p;
    }
}
