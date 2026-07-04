// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Measures each batch-keccak backend both on an idle machine and under CPU contention that mimics Lane A (block
/// execution + prewarming) saturating the cores while the shadow lane hashes. Answers whether GPU offload - which uses
/// near-zero CPU worker threads - wins on system impact under contention even when it loses the isolated latency race.
/// </summary>
/// <remarks>
/// The isolated matrix (<see cref="GpuKeccakBatchBenchmarks"/>) lets the multi-core backend (6a) commandeer every core;
/// in production those cores belong to execution. Here a <see cref="CpuSaturator"/> holds (physical cores - 1) threads
/// busy in steady state for the whole contended run - started in <see cref="GlobalSetup"/>, stopped in
/// <see cref="GlobalCleanup"/>, never restarted per iteration - so the reported latency is what the backend achieves
/// while sharing the machine. Scoped to N in {16384, 65536} and both length profiles to keep the contended run bounded.
/// The per-batch CPU-cost signal and the iGPU-vs-saturator interference are measured separately by
/// <see cref="ContentionProbe"/> (a clean pass outside BenchmarkDotNet), because those need process-wide CPU-time and
/// throughput deltas that do not fit BenchmarkDotNet's per-iteration isolation.
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class ContendedGpuKeccakBatchBenchmarks
{
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() => AddJob(Job.ShortRun
            .WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(5));
    }

    private static readonly KeccakBatchBackend[] _backends = KeccakBatchBackendCatalog.Discover();

    static ContendedGpuKeccakBatchBenchmarks() => AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        KeccakBatchBackendCatalog.DisposeAll(_backends);

    public IEnumerable<KeccakBatchBackend> Backends => _backends;

    [ParamsSource(nameof(Backends))]
    public KeccakBatchBackend Hasher { get; set; } = null!;

    [Params(16384, 65536)]
    public int N { get; set; }

    [Params(GpuKeccakBatchBenchmarks.Profile.Fixed100, GpuKeccakBatchBenchmarks.Profile.TrieMix)]
    public GpuKeccakBatchBenchmarks.Profile Distribution { get; set; }

    /// <summary>Whether background CPU load runs during the measurement (the whole point of this class).</summary>
    [Params(false, true)]
    public bool Contended { get; set; }

    private byte[] _flat = null!;
    private int[] _offsets = null!;
    private ValueHash256[] _outputs = null!;
    private CpuSaturator? _saturator;

    [GlobalSetup]
    public void Setup()
    {
        _offsets = new int[N];
        int total = 0;
        for (int i = 0; i < N; i++)
        {
            int len = Distribution switch
            {
                GpuKeccakBatchBenchmarks.Profile.Fixed100 => 100,
                _ => (i % 3) switch { 0 => 70, 1 => 110, _ => 532 },
            };
            total += len;
            _offsets[i] = total;
        }

        _flat = new byte[total];
        new Random(1).NextBytes(_flat);
        _outputs = new ValueHash256[N];

        if (Contended)
        {
            int workers = Math.Max(1, RuntimeInformation.PhysicalCoreCount - 1);
            _saturator = new CpuSaturator(workers);
        }
    }

    [Benchmark]
    public void HashBatch() => Hasher.Hasher.HashBatch(_flat, _offsets, _outputs);

    [GlobalCleanup]
    public void Cleanup()
    {
        _saturator?.Dispose();
        _saturator = null;
    }
}
