// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Tools.ProfilingMcp.Analysis;
using Nethermind.Tools.ProfilingMcp.EventPipe;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

/// <summary>Samples this test process over EventPipe, so a TraceEvent or runtime update that breaks the samplers fails here.</summary>
[NonParallelizable]
public class LiveEventPipeTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private string _trace = null!;

    [SetUp]
    public void SetUp() => _trace = Path.Combine(Path.GetTempPath(), WorkspacePaths.NewFileName("live", ".nettrace"));

    [TearDown]
    public void TearDown() => File.Delete(_trace);

    [Test]
    [CancelAfter(120_000)]
    public async Task Cpu_sample_finds_a_busy_method(CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = new();
        Task busy = Task.Run(() => Spin(stop.Token), CancellationToken.None);
        try
        {
            await CpuSampler.CollectAsync(Environment.ProcessId, Window, _trace, cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await busy;
        }

        FunctionStat? spin = new NetTraceReader().Read(_trace).Functions.FirstOrDefault(static f => f.Name.EndsWith($"{nameof(LiveEventPipeTests)}.{nameof(Spin)}", StringComparison.Ordinal));

        Assert.That(spin?.OwnTime, Is.GreaterThan(Window.TotalMilliseconds / 4), "the spinning thread is sampled for a good part of the window");
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Alloc_sample_finds_the_allocated_type(CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = new();
        Task allocating = Task.Run(() => Allocate(stop.Token), CancellationToken.None);
        try
        {
            await AllocationSampler.CollectAsync(Environment.ProcessId, Window, _trace, cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await allocating;
        }

        AllocationProfile profile = AllocationSampler.Read(_trace, include: null);

        Assert.That(profile.ByType.Select(static t => t.Name), Has.Some.EndsWith(nameof(Allocated)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Spin(CancellationToken stop)
    {
        long sum = 0;
        while (!stop.IsCancellationRequested)
        {
            for (int i = 0; i < 100_000; i++)
                sum += i ^ sum;
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Allocate(CancellationToken stop)
    {
        Allocated? last = null;
        while (!stop.IsCancellationRequested)
            last = new Allocated(last is null ? 0 : last.A + 1, 0, 0, 0);
        GC.KeepAlive(last);
    }

    private sealed record Allocated(long A, long B, long C, long D);
}
