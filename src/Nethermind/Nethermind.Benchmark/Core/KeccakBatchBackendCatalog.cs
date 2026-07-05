// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Crypto.Gpu;

namespace Nethermind.Benchmarks.Core;

/// <summary>A named batch-keccak backend: the label rendered in a results table and the hasher it dispatches through.</summary>
/// <remarks><see cref="ToString"/> is what appears in the results table, so it must fully identify the backend.</remarks>
public sealed class KeccakBatchBackend(string name, IKeccakBatchHasher hasher)
{
    public string Name { get; } = name;
    public IKeccakBatchHasher Hasher { get; } = hasher;
    public override string ToString() => Name;
}

/// <summary>Discovers the batch-keccak backends present on the host (CPU backends plus one entry per GPU device).</summary>
/// <remarks>
/// Shared by the idle matrix, the contended matrix, and the out-of-BenchmarkDotNet contention probe so all three measure
/// exactly the same backend set. The per-message baseline and multi-core backend (6a) are always present; the vertical
/// multi-buffer kernel (6b) only when <see cref="MultiBufferKeccakBatchHasher.IsSupported"/>; and one entry per non-CPU
/// ILGPU device, each created on that specific device (see <see cref="GpuKeccakBatchHasher.EnumerateDevices"/>). GPU
/// entries hold native accelerator resources; callers dispose them once at process exit.
/// </remarks>
public static class KeccakBatchBackendCatalog
{
    /// <summary>Builds one backend entry per available implementation, including one per discovered GPU device.</summary>
    public static KeccakBatchBackend[] Discover()
    {
        List<KeccakBatchBackend> backends =
        [
            new("PerMessage", new PerMessageKeccakBatchHasher()),
            new("Parallel(6a)", new ParallelKeccakBatchHasher()),
        ];

        if (MultiBufferKeccakBatchHasher.IsSupported)
        {
            backends.Add(new("MultiBuffer(6b)", new MultiBufferKeccakBatchHasher(MultiBufferGroupingStrategy.UniformGroups)));
            // Experiment: does composing the vertical 8-way kernel (6b) with 6a's multi-core work-stealing multiply?
            backends.Add(new("ParallelMultiBuffer(6c)", new ParallelMultiBufferKeccakBatchHasher()));
            // DOP-4 reading of the same variant: does 4 cores of vertical-kernel match full 6a throughput?
            backends.Add(new("ParallelMultiBuffer-DOP4", new ParallelMultiBufferKeccakBatchHasher(4)));
        }

        foreach (GpuDeviceInfo device in GpuKeccakBatchHasher.EnumerateDevices())
        {
            if (GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? gpu, device.Index) && gpu is not null)
            {
                backends.Add(new($"Gpu-{device.Type}({device.Name})", gpu));
            }
        }

        return [.. backends];
    }

    /// <summary>Disposes every backend that owns native resources (the GPU hashers).</summary>
    public static void DisposeAll(KeccakBatchBackend[] backends)
    {
        foreach (KeccakBatchBackend backend in backends) (backend.Hasher as IDisposable)?.Dispose();
    }
}

/// <summary>
/// A background CPU load that mimics Lane A (block execution + prewarming) saturating the machine while the shadow lane
/// hashes: a fixed number of dedicated threads each hash a private buffer in a tight loop and publish an iteration count.
/// </summary>
/// <remarks>
/// Realistic (not a pure-ALU spin): each worker runs the production per-message keccak over its own 4 KiB buffer, so it
/// touches the same code path and exercises cache/memory the way execution would, rather than pegging an ALU with a
/// no-op loop. The workers are started once and run to steady state until <see cref="Dispose"/>; a benchmark must NOT
/// start/stop them per iteration. The published per-worker iteration counters let a caller measure whether a co-running
/// iGPU steals memory-bus/power from these threads (their throughput drops when the iGPU is active).
/// </remarks>
public sealed class CpuSaturator : IDisposable
{
    private readonly Thread[] _threads;
    private readonly long[] _iterations; // one padded slot per worker to avoid false sharing on the counters
    private volatile bool _stop;

    private const int CounterStride = 8; // 8 longs = 64 bytes = one cache line per worker counter

    /// <summary>Starts <paramref name="workerCount"/> background hashing threads immediately.</summary>
    public CpuSaturator(int workerCount)
    {
        _threads = new Thread[workerCount];
        _iterations = new long[workerCount * CounterStride];
        for (int i = 0; i < workerCount; i++)
        {
            int worker = i;
            Thread t = new(() => Work(worker)) { IsBackground = true, Name = $"ksat-{worker}", Priority = ThreadPriority.Normal };
            _threads[i] = t;
            t.Start();
        }
    }

    /// <summary>Total iterations completed across all workers since start; snapshot two of these to get throughput over an interval.</summary>
    public long TotalIterations()
    {
        long sum = 0;
        for (int i = 0; i < _threads.Length; i++) sum += Volatile.Read(ref _iterations[i * CounterStride]);
        return sum;
    }

    private void Work(int worker)
    {
        byte[] buffer = new byte[4096];
        new Random(worker + 1).NextBytes(buffer);
        Span<byte> output = stackalloc byte[32];
        long count = 0;
        while (!_stop)
        {
            // A block of hashes between counter publishes keeps the volatile write off the hottest path while still
            // updating often enough for interval throughput sampling.
            for (int k = 0; k < 256; k++)
            {
                KeccakHash.ComputeHash(buffer, output);
                buffer[0] = output[0]; // feed forward so the JIT cannot hoist the hash out of the loop
            }
            count += 256;
            Volatile.Write(ref _iterations[worker * CounterStride], count);
        }
    }

    public void Dispose()
    {
        _stop = true;
        foreach (Thread t in _threads) t.Join();
    }
}
