// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmark.Test")]

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

public class GasBenchmarkConfig : ManualConfig
{
    internal static bool InProcess { get; set; }

    /// <summary>1-based chunk index (e.g. 1 means first chunk). 0 = no chunking.</summary>
    internal static int ChunkIndex { get; set; }

    /// <summary>Total number of chunks to split scenarios into.</summary>
    internal static int ChunkTotal { get; set; }

    /// <summary>Override for BDN warmup count. Null = 3 (our default).</summary>
    internal static int? WarmupCount { get; set; }

    /// <summary>Override for BDN iteration count. Null = 1 (our default).</summary>
    internal static int? IterationCount { get; set; }

    /// <summary>Override for BDN launch count. Null = 1 (our default).</summary>
    internal static int? LaunchCount { get; set; }

    public GasBenchmarkConfig()
    {
        Job job = Job.MediumRun
            .WithLaunchCount(LaunchCount ?? 1)
            .WithWarmupCount(WarmupCount ?? 3)
            .WithIterationCount(IterationCount ?? 1);

        if (InProcess)
        {
            job = job.WithToolchain(InProcessEmitToolchain.Instance);
        }

        AddJob(job);

        // Nethermind's dependency tree is large; the default 120s build timeout is too short for out-of-process mode.
        WithBuildTimeout(TimeSpan.FromMinutes(10));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumnProvider(new GasBenchmarkColumnProvider());
        AddExporter(JsonExporter.Full);
    }
}
