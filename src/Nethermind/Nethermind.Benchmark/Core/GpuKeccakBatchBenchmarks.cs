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

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Benchmarks every batch keccak256 backend available on the host across a batch-size and length-profile matrix,
/// including one entry per GPU accelerator discovered at runtime. The backend list is built by probing the machine,
/// not hardcoded, so the same benchmark self-describes on a CUDA+OpenCL dev box, an OpenCL-iGPU-only CI runner, or a
/// GPU-less box.
/// </summary>
/// <remarks>
/// Backend discovery (see <see cref="Backends"/>): the per-message baseline and the multi-core backend (6a) are always
/// present; the vertical multi-buffer kernel (6b) is included only when <see cref="MultiBufferKeccakBatchHasher.IsSupported"/>;
/// and one entry is added per non-CPU ILGPU device (<see cref="GpuKeccakBatchHasher.EnumerateDevices"/>), each created on
/// that specific device and labelled with its driver name. Missing hardware simply omits the entry - no zero rows, no
/// skips. GPU backends carry a serialized host-device transfer per dispatch (correct for the shadow lane, see the
/// <see cref="GpuKeccakBatchHasher"/> remarks), so the reported time is end-to-end dispatch, not kernel-only. GPU
/// hashers are created once and disposed at process exit (they are shared across all matrix cases).
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class GpuKeccakBatchBenchmarks
{
    // GPU dispatch amortizes over invocations, so a short in-process job is used with enough iterations to cover the
    // one-time device warmup: a slightly longer minimum iteration time than ShortRun, still bounded for tens-of-minutes
    // total across the full matrix.
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() => AddJob(Job.ShortRun
            .WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(5));
    }

    // Built once at type load so the same instances back the ParamsSource grid across every case. GPU entries hold
    // native accelerator resources; they are shared across all parameter combinations, so disposal is registered at
    // process exit (not per-case GlobalCleanup, which would free them before later cases run).
    private static readonly KeccakBatchBackend[] _backends = KeccakBatchBackendCatalog.Discover();

    static GpuKeccakBatchBenchmarks() => AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        KeccakBatchBackendCatalog.DisposeAll(_backends);

    /// <summary>Length distribution across the batch.</summary>
    public enum Profile
    {
        /// <summary>Every message 100 bytes (one full sponge block plus a pad block); comparable with the earlier rough numbers.</summary>
        Fixed100,

        /// <summary>Trie-level mix: repeating 70/110/532-byte messages (leaf / branch-ish / large-node shapes).</summary>
        TrieMix,
    }

    public IEnumerable<KeccakBatchBackend> Backends => _backends;

    [ParamsSource(nameof(Backends))]
    public KeccakBatchBackend Hasher { get; set; } = null!;

    [Params(4096, 16384, 65536, 262144)]
    public int N { get; set; }

    [Params(Profile.Fixed100, Profile.TrieMix)]
    public Profile Distribution { get; set; }

    private byte[] _flat = null!;
    private int[] _offsets = null!;
    private ValueHash256[] _outputs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _offsets = new int[N];
        int total = 0;
        for (int i = 0; i < N; i++)
        {
            int len = Distribution switch
            {
                Profile.Fixed100 => 100,
                _ => (i % 3) switch { 0 => 70, 1 => 110, _ => 532 },
            };
            total += len;
            _offsets[i] = total;
        }

        _flat = new byte[total];
        new Random(1).NextBytes(_flat);
        _outputs = new ValueHash256[N];
    }

    [Benchmark]
    public void HashBatch() => Hasher.Hasher.HashBatch(_flat, _offsets, _outputs);
}
