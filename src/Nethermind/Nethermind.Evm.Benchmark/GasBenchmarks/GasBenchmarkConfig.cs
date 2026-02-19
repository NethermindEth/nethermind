// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    public GasBenchmarkConfig()
    {
        Job job = Job.MediumRun.WithLaunchCount(1).WithIterationCount(10);

        if (InProcess)
        {
            job = job.WithToolchain(InProcessEmitToolchain.Instance);
        }

        AddJob(job);
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumnProvider(new GasBenchmarkColumnProvider());
        AddExporter(JsonExporter.Full);
    }
}
