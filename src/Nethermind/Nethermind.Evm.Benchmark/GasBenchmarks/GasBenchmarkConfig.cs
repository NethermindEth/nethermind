// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

public class GasBenchmarkConfig : ManualConfig
{
    internal static bool InProcess { get; set; }

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
