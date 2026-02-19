// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

[TestFixture]
public class GasBenchmarkConfigTests
{
    [TearDown]
    public void TearDown()
    {
        // Reset static state after each test
        GasBenchmarkConfig.InProcess = false;
        GasBenchmarkConfig.ChunkIndex = 0;
        GasBenchmarkConfig.ChunkTotal = 0;
    }

    [Test]
    public void Config_Has_MemoryDiagnoser()
    {
        GasBenchmarkConfig config = new();

        bool hasMemoryDiagnoser = config.GetDiagnosers().Any(d => d is MemoryDiagnoser);

        Assert.That(hasMemoryDiagnoser, Is.True);
    }

    [Test]
    public void Config_Has_JsonExporter()
    {
        GasBenchmarkConfig config = new();

        bool hasJsonExporter = config.GetExporters().Any(e => e is JsonExporter);

        Assert.That(hasJsonExporter, Is.True);
    }

    [Test]
    public void Config_Has_GasBenchmarkColumnProvider()
    {
        GasBenchmarkConfig config = new();

        bool hasColumnProvider = config.GetColumnProviders().Any(p => p is GasBenchmarkColumnProvider);

        Assert.That(hasColumnProvider, Is.True);
    }

    [Test]
    public void Config_Has_Job_With_LaunchCount_1()
    {
        GasBenchmarkConfig config = new();

        Job job = config.GetJobs().First();

        Assert.That(job.Run.LaunchCount, Is.EqualTo(1));
    }

    [Test]
    public void Config_Has_Job_With_IterationCount_10()
    {
        GasBenchmarkConfig config = new();

        Job job = config.GetJobs().First();

        Assert.That(job.Run.IterationCount, Is.EqualTo(10));
    }

    [Test]
    public void Config_Uses_InProcessToolchain_When_InProcess_Is_True()
    {
        GasBenchmarkConfig.InProcess = true;

        GasBenchmarkConfig config = new();

        Job job = config.GetJobs().First();
        Assert.That(job.Infrastructure.Toolchain, Is.SameAs(InProcessEmitToolchain.Instance));
    }

    [Test]
    public void Config_Does_Not_Use_InProcessToolchain_When_InProcess_Is_False()
    {
        GasBenchmarkConfig.InProcess = false;

        GasBenchmarkConfig config = new();

        Job job = config.GetJobs().First();
        Assert.That(job.Infrastructure.Toolchain, Is.Not.SameAs(InProcessEmitToolchain.Instance));
    }

    [Test]
    public void ChunkIndex_And_ChunkTotal_Default_To_Zero()
    {
        Assert.That(GasBenchmarkConfig.ChunkIndex, Is.EqualTo(0));
        Assert.That(GasBenchmarkConfig.ChunkTotal, Is.EqualTo(0));
    }

    [Test]
    public void ChunkIndex_And_ChunkTotal_Can_Be_Set()
    {
        GasBenchmarkConfig.ChunkIndex = 3;
        GasBenchmarkConfig.ChunkTotal = 5;

        Assert.That(GasBenchmarkConfig.ChunkIndex, Is.EqualTo(3));
        Assert.That(GasBenchmarkConfig.ChunkTotal, Is.EqualTo(5));
    }

    [Test]
    public void InProcess_Defaults_To_False()
    {
        Assert.That(GasBenchmarkConfig.InProcess, Is.False);
    }
}
