// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

public class GasBenchmarkConfig : ManualConfig
{
    public GasBenchmarkConfig()
    {
        AddJob(Job.MediumRun.WithIterationCount(10));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumnProvider(new GasBenchmarkColumnProvider());
        AddExporter(JsonExporter.Full);
    }
}
