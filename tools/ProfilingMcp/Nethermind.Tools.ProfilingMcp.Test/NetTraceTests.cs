// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Tools.ProfilingMcp.DotTrace;
using Nethermind.Tools.ProfilingMcp.EventPipe;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class NetTraceTests
{
    [TestCase("System.Private.CoreLib!System.Threading.Monitor.Wait(class System.Object,int32)", ExpectedResult = "System.Threading.Monitor.Wait")]
    [TestCase("System.Private.CoreLib!System.Runtime.CompilerServices.TaskAwaiter`1[System.Int32].GetResult()", ExpectedResult = "System.Runtime.CompilerServices.TaskAwaiter`1.GetResult")]
    [TestCase("Nethermind.Consensus!Scheduler.Activity`1+<Do>d__14[Wrapper+Request`1[System.Int32]].MoveNext()", ExpectedResult = "Scheduler.Activity`1+<Do>d__14.MoveNext")]
    [TestCase("?", ExpectedResult = "?")]
    public string Cleans_frame_names_to_the_dottrace_form(string frame) => NetTraceReader.CleanFrameName(frame);

    [Test]
    public void Diff_subtracts_baseline_and_keeps_methods_present_in_one_trace_only()
    {
        FunctionStat[] baseline = [new("A", 10, 4, null, 1, []), new("Gone", 3, 3, null, 1, [])];
        FunctionStat[] current = [new("A", 7, 5, null, 1, []), new("New", 2, 2, null, 1, [])];

        Dictionary<string, (double, double)> deltas = TraceTools.Diff(baseline, current).ToDictionary(static f => f.Name, static f => (f.TotalTime, f.OwnTime));

        Assert.That(deltas, Is.EquivalentTo(new Dictionary<string, (double, double)>
        {
            ["A"] = (-3, 1),
            ["Gone"] = (-3, -3),
            ["New"] = (2, 2),
        }));
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Samples_own_process_and_reads_methods_and_exceptions(CancellationToken cancellationToken)
    {
        string trace = Path.Combine(Path.GetTempPath(), $"self-{Guid.NewGuid():N}.nettrace");
        using CancellationTokenSource stop = new();
        Thread worker = new(() => BusyWork(stop.Token)) { IsBackground = true };
        worker.Start();
        try
        {
            await CpuSampler.CollectAsync(Environment.ProcessId, TimeSpan.FromSeconds(2), trace, cancellationToken);
            stop.Cancel();
            worker.Join();

            NetTraceProfile profile = new NetTraceReader().Read(trace);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(profile.Functions.Single(static f => f.Name.EndsWith(".BusyWork", StringComparison.Ordinal)).TotalTime, Is.GreaterThan(500));
                Assert.That(profile.Exceptions.Select(static e => e.Type), Does.Contain(typeof(InvalidOperationException).FullName));
                Assert.That(profile.Threads, Is.GreaterThan(1));
                Assert.That(profile.Gc.CountsByGeneration.GetValueOrDefault(2), Is.GreaterThan(0));
                Assert.That(profile.Gc.MaxPauseMs, Is.GreaterThan(0));
                Assert.That(profile.Gc.TotalPauseMs, Is.LessThanOrEqualTo(profile.Gc.CountsByGeneration.Values.Sum() * profile.Gc.MaxPauseMs),
                    "pauses of non-GC suspensions must not be counted");
            }
        }
        finally
        {
            File.Delete(trace);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BusyWork(CancellationToken token)
    {
        long iterations = 0;
        while (!token.IsCancellationRequested)
        {
            if (++iterations % 50_000_000 == 0)
            {
                GC.Collect();
                try
                {
                    throw new InvalidOperationException("sampled");
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }
}
