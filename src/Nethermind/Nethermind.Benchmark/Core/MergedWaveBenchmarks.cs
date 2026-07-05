// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core.Crypto;
using Nethermind.Crypto.Gpu;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Measures the merged cross-trie wave (<see cref="BatchedTrieCommitter.UpdateRootHashesBatched(IReadOnlyList{PatriciaTree}, IKeccakBatchHasher)"/>)
/// against per-trie independent waves (the pre-merge across-tries approach), over realistic block shapes: many
/// storage-writing accounts whose slot counts follow a skewed distribution (mostly 1-4 slots, a few 100+).
/// </summary>
/// <remarks>
/// Reproduces the adoption evidence for the merged wave now wired into <c>BalStateRootCalculator</c>: (i) the merged wave
/// must not be slower than per-trie waves on CPU hashers, and (ii) its first wave step must reach a materially wider
/// batch than any per-trie wave (the GPU-enablement property). The width distribution is printed once from
/// <see cref="Setup"/> via the internal wave-step observer seam. Each case rebuilds the tries per invocation because the
/// wave mutates node <c>Keccak</c>/<c>FullRlp</c>; build cost is included in both arms equally, so the comparison is fair.
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class MergedWaveBenchmarks
{
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() => AddJob(Job.ShortRun
            .WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(5));
    }

    private static readonly KeccakBatchBackend[] _backends = BuildBackends();

    static MergedWaveBenchmarks() => AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        KeccakBatchBackendCatalog.DisposeAll(_backends);

    // Per-message, multi-core (6a), and the CUDA-threshold router if a GPU is present. The router models the production
    // ThresholdKeccakBatchHasher: batches >= GpuMinBatch go to the GPU, smaller to the CPU (6a) fallback.
    private static KeccakBatchBackend[] BuildBackends()
    {
        List<KeccakBatchBackend> backends =
        [
            new("PerMessage", new PerMessageKeccakBatchHasher()),
            new("Parallel(6a)", new ParallelKeccakBatchHasher()),
        ];

        foreach (GpuDeviceInfo device in GpuKeccakBatchHasher.EnumerateDevices())
        {
            if (GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? gpu, device.Index) && gpu is not null)
            {
                backends.Add(new($"CudaThreshold-{device.Type}({device.Name})",
                    new ThresholdKeccakBatchHasher(gpu, new ParallelKeccakBatchHasher(), 65536, Nethermind.Logging.LimboLogs.Instance)));
            }
        }

        return [.. backends];
    }

    public IEnumerable<KeccakBatchBackend> Backends => _backends;

    [ParamsSource(nameof(Backends))]
    public KeccakBatchBackend Hasher { get; set; } = null!;

    /// <summary>Storage-writing account count for the synthesized block.</summary>
    [Params(100, 400, 1600)]
    public int Accounts { get; set; }

    private int[] _slotCounts = null!;
    private bool _printedWidths;

    [GlobalSetup]
    public void Setup()
    {
        // Skewed slot-count distribution: ~85% tiny (1-4 slots), ~12% medium (5-30), ~3% large (100-400).
        Random rng = new(42);
        _slotCounts = new int[Accounts];
        for (int i = 0; i < Accounts; i++)
        {
            double roll = rng.NextDouble();
            _slotCounts[i] = roll < 0.85 ? rng.Next(1, 5)
                : roll < 0.97 ? rng.Next(5, 31)
                : rng.Next(100, 401);
        }

        // Print the merged-wave width distribution once (per-invocation width is hasher-independent, so record it here).
        if (!_printedWidths)
        {
            _printedWidths = true;
            PatriciaTree[] tries = BuildTries();
            List<(int step, int width)> steps = [];
            BatchedTrieCommitter.UpdateRootHashesBatched(tries, new PerMessageKeccakBatchHasher(), (step, width) => steps.Add((step, width)));
            Console.WriteLine($"[merged-wave widths] accounts={Accounts} totalSlots={SumSlots()}");
            foreach ((int step, int width) in steps) Console.WriteLine($"[merged-wave widths]   step {step}: {width}");
        }
    }

    private int SumSlots()
    {
        int total = 0;
        foreach (int c in _slotCounts) total += c;
        return total;
    }

    /// <summary>Builds one fresh, uncommitted storage trie per account from its slot writes; nodes stay dirty.</summary>
    private PatriciaTree[] BuildTries()
    {
        PatriciaTree[] tries = new PatriciaTree[Accounts];
        for (int a = 0; a < Accounts; a++)
        {
            PatriciaTree tree = new(new MemDb());
            int slots = _slotCounts[a];
            for (int s = 0; s < slots; s++)
            {
                // Distinct 32-byte slot keys per account; 32-byte values so leaves are keccak refs (realistic storage).
                UInt256 slot = (UInt256)((ulong)(a * 100_000 + s + 1));
                byte[] key = slot.ToBigEndian();
                byte[] value = new byte[32];
                BitConverter.TryWriteBytes(value, (ulong)(s + 1));
                tree.Set(key, value);
            }

            tries[a] = tree;
        }

        return tries;
    }

    [Benchmark(Baseline = true)]
    public void PerTrieWaves()
    {
        // Pre-merge shape: each storage trie hashed by its own independent wave (one HashBatch per level PER TRIE).
        PatriciaTree[] tries = BuildTries();
        foreach (PatriciaTree tree in tries) BatchedTrieCommitter.UpdateRootHashBatched(tree, Hasher.Hasher);
    }

    [Benchmark]
    public void MergedWave()
    {
        // Merged shape: every trie's next-deepest level concatenated into ONE HashBatch per wave step.
        PatriciaTree[] tries = BuildTries();
        BatchedTrieCommitter.UpdateRootHashesBatched(tries, Hasher.Hasher);
    }
}
