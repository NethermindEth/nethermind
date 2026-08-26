// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Summarize the runtime events in a dotnet-trace .nettrace: GC pauses, lock contention, exceptions.
// A CPU profile shows none of these, so this is what the EventPipe sidecar of the benchmark
// workflows is collected for (see scripts/rpc-bench/README.md, "dotnet-trace sidecar").
//
//   dotnet run scripts/nettrace-report.cs -- <file.nettrace> [--top N]
//
// A file-based app rather than a project: it stays out of every solution and out of the tools
// matrix, like the sibling shell reporters. The generated project would otherwise inherit the
// repository's central package management, which forbids a version on the reference itself.
#:property ManagePackageVersionsCentrally=false
#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.1.16

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run scripts/nettrace-report.cs -- <file.nettrace> [--top N]");
    return 2;
}

string path = args[0];
int top = 8;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--top" && int.TryParse(args[i + 1], out int parsed) && parsed > 0) top = parsed;
}

if (!File.Exists(path))
{
    Console.Error.WriteLine($"error: no such file: {path}");
    return 2;
}
if (new FileInfo(path).Length == 0)
{
    Console.Error.WriteLine($"error: {path} is empty");
    return 2;
}

List<(int Gen, string Type, string Reason, double PauseMs, double PromotedMb, double TimeMs)> gcs = [];
List<double> contentionMs = [];
double contentionTotalMs = 0;
double contentionMaxMs = 0;
int contentionStarts = 0, exceptions = 0, threadPoolAdjustments = 0;
double firstEventMs = double.NaN, lastEventMs = 0;
long eventCount = 0;

try
{
    using EventPipeEventSource source = new(path);

    source.AllEvents += e =>
    {
        eventCount++;
        if (double.IsNaN(firstEventMs)) firstEventMs = e.TimeStampRelativeMSec;
        lastEventMs = e.TimeStampRelativeMSec;
    };

    // The GC view is assembled by the analysis layer from the raw start/stop/heap-stats events.
    source.NeedLoadedDotNetRuntimes();
    source.AddCallbackOnProcessStart(proc =>
    {
        proc.AddCallbackOnDotNetRuntimeLoad(runtime =>
        {
            runtime.GCEnd += (_, gc) => gcs.Add((
                gc.Generation,
                gc.Type.ToString(),
                gc.Reason.ToString(),
                gc.PauseDurationMSec,
                gc.PromotedMB,
                gc.PauseStartRelativeMSec));
        });
    });

    source.Clr.ContentionStart += _ => contentionStarts++;
    source.Clr.ContentionStop += (ContentionStopTraceData e) =>
    {
        double ms = e.DurationNs / 1_000_000.0;
        contentionMs.Add(ms);
        contentionTotalMs += ms;
        if (ms > contentionMaxMs) contentionMaxMs = ms;
    };
    source.Clr.ExceptionStart += _ => exceptions++;
    source.Clr.ThreadPoolWorkerThreadAdjustmentAdjustment += _ => threadPoolAdjustments++;

    source.Process();
}
catch (Exception ex)
{
    // A trace stopped while the process was still running is routinely truncated at the tail;
    // report whatever was aggregated rather than losing the run.
    Console.Error.WriteLine($"warning: processing stopped early: {ex.GetType().Name}: {ex.Message}");
}

double windowMs = double.IsNaN(firstEventMs) ? 0 : lastEventMs - firstEventMs;
Console.WriteLine($"# {Path.GetFileName(path)}");
Console.WriteLine();
Console.WriteLine($"- events: {eventCount:N0}, window: {windowMs / 1000.0:F1} s");
Console.WriteLine();

Console.WriteLine("## GC");
Console.WriteLine();
if (gcs.Count == 0)
{
    Console.WriteLine("no GC events");
}
else
{
    double pauseTotal = gcs.Sum(g => g.PauseMs);
    Console.WriteLine($"- {gcs.Count} collections, pause total {pauseTotal:F1} ms" +
                      (windowMs > 0 ? $" ({pauseTotal / windowMs * 100:F2}% of the window)" : ""));
    foreach (IGrouping<int, (int Gen, string Type, string Reason, double PauseMs, double PromotedMb, double TimeMs)> g
             in gcs.GroupBy(g => g.Gen).OrderBy(g => g.Key))
    {
        Console.WriteLine($"- gen{g.Key}: {g.Count()} collections, pause {g.Sum(x => x.PauseMs):F1} ms " +
                          $"(max {g.Max(x => x.PauseMs):F1} ms, promoted {g.Sum(x => x.PromotedMb):F0} MB)");
    }
    Console.WriteLine();
    Console.WriteLine("| # | at s | gen | type | reason | pause ms | promoted MB |");
    Console.WriteLine("|---|---|---|---|---|---|---|");
    int rank = 0;
    foreach ((int Gen, string Type, string Reason, double PauseMs, double PromotedMb, double TimeMs) gc
             in gcs.OrderByDescending(g => g.PauseMs).Take(top))
    {
        Console.WriteLine($"| {++rank} | {gc.TimeMs / 1000.0:F1} | {gc.Gen} | {gc.Type} | {gc.Reason} | {gc.PauseMs:F1} | {gc.PromotedMb:F0} |");
    }
}

Console.WriteLine();
Console.WriteLine("## Lock contention");
Console.WriteLine();
if (contentionMs.Count == 0)
{
    Console.WriteLine($"no contention stop events ({contentionStarts} starts seen)");
}
else
{
    contentionMs.Sort();
    double Percentile(double q) => contentionMs[Math.Min(contentionMs.Count - 1, (int)(contentionMs.Count * q))];
    Console.WriteLine($"- {contentionMs.Count:N0} waits ({contentionStarts:N0} starts), total {contentionTotalMs:F1} ms" +
                      (windowMs > 0 ? $" ({contentionTotalMs / windowMs * 100:F2}% of one core-window)" : ""));
    Console.WriteLine($"- per wait: p50 {Percentile(0.50):F3} ms, p90 {Percentile(0.90):F3} ms, " +
                      $"p99 {Percentile(0.99):F3} ms, max {contentionMaxMs:F1} ms");
    Console.WriteLine();
    Console.WriteLine("_Blocked time only. CPU burnt spinning before a wait blocks is not here — a sampling profiler " +
                      "attributes that to `Monitor.Enter_Slowpath` instead, so the two disagree by design._");
}

Console.WriteLine();
Console.WriteLine("## Other");
Console.WriteLine();
Console.WriteLine($"- exceptions thrown: {exceptions:N0}");
Console.WriteLine($"- thread pool worker adjustments: {threadPoolAdjustments:N0}");
Console.WriteLine();
Console.WriteLine("_Contention events carry no call stacks at informational level; the sidecar collects them at " +
                  "verbose level, and the owning stacks are readable in PerfView._");
return 0;
