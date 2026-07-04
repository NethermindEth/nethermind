// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using MemoryMarshal = System.Runtime.InteropServices.MemoryMarshal;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Benchmarks the batch keccak256 backends over batch-size and length-mix matrices: the per-message baseline, the
/// multi-core backend (6a), and the experimental vertical multi-buffer kernel (6b, both grouping strategies).
/// </summary>
/// <remarks>
/// Also compares two 6a partitionings on the non-uniform trie mix: the production per-index work-stealing partition
/// (<see cref="ParallelKeccakBatchHasher"/>) vs a benchmark-local static contiguous-slice partition, which can leave
/// one worker holding the long messages. Per-index measured faster and is the adopted production strategy; the
/// static-slice variant is kept here only to reproduce the comparison.
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class KeccakBatchBenchmarks
{
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() => AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
    }

    /// <summary>Named length distribution across the batch.</summary>
    public enum Mix
    {
        /// <summary>All messages 32 bytes (one block).</summary>
        Fixed32,

        /// <summary>All messages 136 bytes (one full block plus a pad block - two blocks).</summary>
        Fixed136,

        /// <summary>All messages 532 bytes (532/136 + 1 = 4 sponge blocks after padding).</summary>
        Fixed532,

        /// <summary>Trie-level mix: repeating 70/110/532-byte messages (leaf/branch-ish/large-node shapes).</summary>
        TrieMix,
    }

    [Params(64, 1024, 16384)]
    public int N { get; set; }

    [Params(Mix.Fixed32, Mix.Fixed136, Mix.Fixed532, Mix.TrieMix)]
    public Mix Distribution { get; set; }

    private byte[] _flat = null!;
    private int[] _offsets = null!;
    private ValueHash256[] _outputs = null!;

    private readonly PerMessageKeccakBatchHasher _perMessage = new();
    private readonly ParallelKeccakBatchHasher _parallel = new(parallelThreshold: 1);
    private readonly MultiBufferKeccakBatchHasher _multiUniform = new(MultiBufferGroupingStrategy.UniformGroups);
    private readonly MultiBufferKeccakBatchHasher _multiRunToMax = new(MultiBufferGroupingStrategy.RunToMaxSnapshots);

    [GlobalSetup]
    public void Setup()
    {
        int[] lengths = new int[N];
        for (int i = 0; i < N; i++)
        {
            lengths[i] = Distribution switch
            {
                Mix.Fixed32 => 32,
                Mix.Fixed136 => 136,
                Mix.Fixed532 => 532,
                _ => (i % 3) switch { 0 => 70, 1 => 110, _ => 532 },
            };
        }

        int total = 0;
        _offsets = new int[N];
        for (int i = 0; i < N; i++)
        {
            total += lengths[i];
            _offsets[i] = total;
        }

        _flat = new byte[total];
        new Random(1).NextBytes(_flat);
        _outputs = new ValueHash256[N];
    }

    [Benchmark(Baseline = true)]
    public void PerMessage() => _perMessage.HashBatch(_flat, _offsets, _outputs);

    // Production 6a: per-index work-stealing partition (the adopted strategy).
    [Benchmark]
    public void Parallel_PerIndexStealing() => _parallel.HashBatch(_flat, _offsets, _outputs);

    // Experimental 6a: static contiguous slice per worker (the rejected strategy), kept to reproduce the comparison.
    [Benchmark]
    public void Parallel_StaticSlice() => HashStaticSlice(_flat, _offsets, _outputs);

    [Benchmark]
    public void MultiBuffer_UniformGroups() => _multiUniform.HashBatch(_flat, _offsets, _outputs);

    [Benchmark]
    public void MultiBuffer_RunToMax() => _multiRunToMax.HashBatch(_flat, _offsets, _outputs);

    // Experimental static contiguous-slice 6a partitioning: one disjoint [start, end) slice per worker. A few long
    // messages can pin the worker owning their slice; kept only to reproduce the comparison against the production
    // per-index work-stealing partition, which measured faster on non-uniform lengths.
    private static unsafe void HashStaticSlice(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
    {
        int count = offsets.Length;
        int workers = Math.Min(count, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16.MaxDegreeOfParallelism);
        // The fixed block must enclose the synchronous For so the pins outlive every worker's pointer dereference,
        // mirroring the production hasher.
        fixed (byte* flatPtr = flat)
        fixed (int* offsetsPtr = offsets)
        fixed (ValueHash256* outputsPtr = outputs)
        {
            View view = new(flatPtr, offsetsPtr, outputsPtr, count, workers);
            ParallelUnbalancedWork.For(
                0,
                workers,
                RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                view,
                static (worker, v) =>
                {
                    int from = (int)((long)worker * v.Count / v.Workers);
                    int to = (int)((long)(worker + 1) * v.Count / v.Workers);
                    for (int i = from; i < to; i++)
                    {
                        int start = i == 0 ? 0 : v.Offsets[i - 1];
                        int end = v.Offsets[i];
                        ReadOnlySpan<byte> input = new(v.Flat + start, end - start);
                        Span<byte> output = MemoryMarshal.AsBytes(new Span<ValueHash256>(v.Outputs + i, 1));
                        KeccakHash.ComputeHash(input, output);
                    }
                    return v;
                },
                static _ => { });
        }
    }

    private readonly unsafe struct View(byte* flat, int* offsets, ValueHash256* outputs, int count, int workers)
    {
        public byte* Flat { get; } = flat;
        public int* Offsets { get; } = offsets;
        public ValueHash256* Outputs { get; } = outputs;
        public int Count { get; } = count;
        public int Workers { get; } = workers;
    }
}
