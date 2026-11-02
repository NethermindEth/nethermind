// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>Summary of one runtime counter over the collection window.</summary>
/// <param name="IsRate">True for counters reporting an increment per interval (e.g. allocation rate), false for gauges (e.g. heap size).</param>
public sealed record CounterSummary(string Name, string DisplayName, string Units, bool IsRate, double Min, double Average, double Max, double Last);

/// <summary>Garbage collections observed through the runtime's native GC events.</summary>
/// <param name="CountsByGeneration">Number of GCs that collected up to each generation (0, 1, 2).</param>
/// <param name="TotalPauseMs">Time managed threads were suspended for GCs.</param>
/// <param name="LastHeapBytes">Generation sizes after the last GC (gen0, gen1, gen2, LOH, POH), or null if no GC finished.</param>
public sealed record GcSummary(
    IReadOnlyDictionary<int, int> CountsByGeneration,
    IReadOnlyDictionary<string, int> CountsByReason,
    double TotalPauseMs,
    double MaxPauseMs,
    IReadOnlyDictionary<string, long>? LastHeapBytes,
    long LastPromotedBytes);

/// <summary>Allocations of one type sampled from <c>GCAllocationTick</c> events.</summary>
/// <param name="SampledBytes">Bytes attributed to the type; the runtime raises one event roughly per 100 KB allocated, so this approximates allocated bytes.</param>
public sealed record AllocationStat(string TypeName, long Events, long SampledBytes);

/// <summary>
/// Collects runtime diagnostics from a live .NET process over EventPipe.
/// </summary>
internal static class EventPipeCollector
{
    /// <remarks>
    /// Counters come from the managed <c>System.Runtime</c> EventSource, which apps can switch off with
    /// <c>System.Diagnostics.Tracing.EventSource.IsSupported=false</c> (Nethermind does). GC events come from the runtime's
    /// native provider and are available either way.
    /// </remarks>
    public static async Task<(IReadOnlyList<CounterSummary> Counters, GcSummary Gc)> CollectCountersAsync(int pid, TimeSpan duration, int intervalSeconds, CancellationToken cancellationToken)
    {
        Dictionary<string, (string DisplayName, string Units, bool IsRate, List<double> Values)> samples = [];
        EventPipeProvider counters = new("System.Runtime", EventLevel.Informational, (long)EventKeywords.All,
            new Dictionary<string, string> { ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture) });
        EventPipeProvider gcEvents = new(ClrTraceEventParser.ProviderName, EventLevel.Informational, (long)ClrTraceEventParser.Keywords.GC);

        Dictionary<int, int> byGeneration = [];
        Dictionary<string, int> byReason = [];
        double totalPauseMs = 0;
        double maxPauseMs = 0;
        double? suspendStartMs = null;
        Dictionary<string, long>? lastHeap = null;
        long lastPromoted = 0;

        await RunSessionAsync(pid, [counters, gcEvents], duration, source =>
        {
            source.Clr.GCStart += data =>
            {
                byGeneration[data.Depth] = byGeneration.GetValueOrDefault(data.Depth) + 1;
                string reason = data.Reason.ToString();
                byReason[reason] = byReason.GetValueOrDefault(reason) + 1;
            };
            source.Clr.GCSuspendEEStart += data => suspendStartMs = data.TimeStampRelativeMSec;
            source.Clr.GCRestartEEStop += data =>
            {
                if (suspendStartMs is not double start)
                    return;
                double pause = data.TimeStampRelativeMSec - start;
                totalPauseMs += pause;
                maxPauseMs = Math.Max(maxPauseMs, pause);
                suspendStartMs = null;
            };
            source.Clr.GCHeapStats += data =>
            {
                lastHeap = new Dictionary<string, long>
                {
                    ["gen0"] = data.GenerationSize0,
                    ["gen1"] = data.GenerationSize1,
                    ["gen2"] = data.GenerationSize2,
                    ["LOH"] = data.GenerationSize3,
                    ["POH"] = data.GenerationSize4,
                };
                lastPromoted = data.TotalPromotedSize0 + data.TotalPromotedSize1 + data.TotalPromotedSize2 + data.TotalPromotedSize3;
            };
            source.Dynamic.All += traceEvent =>
            {
                if (traceEvent.EventName != "EventCounters" ||
                    traceEvent.PayloadValue(0) is not IDictionary<string, object> outer ||
                    !outer.TryGetValue("Payload", out object? inner) || inner is not IDictionary<string, object> payload)
                    return;

                string name = Field(payload, "Name") ?? "?";
                bool isRate = Field(payload, "CounterType") == "Sum";
                double value = payload.TryGetValue(isRate ? "Increment" : "Mean", out object? raw) ? Convert.ToDouble(raw, CultureInfo.InvariantCulture) : 0;
                if (!samples.TryGetValue(name, out (string DisplayName, string Units, bool IsRate, List<double> Values) series))
                {
                    series = (Field(payload, "DisplayName") ?? name, Field(payload, "DisplayUnits") ?? "", isRate, []);
                    samples[name] = series;
                }
                series.Values.Add(value);
            };
        }, cancellationToken);

        List<CounterSummary> summaries = [];
        foreach ((string name, (string displayName, string units, bool isRate, List<double> values)) in samples)
        {
            if (values.Count > 0)
                summaries.Add(new CounterSummary(name, displayName, units, isRate, values.Min(), values.Average(), values.Max(), values[^1]));
        }

        summaries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return (summaries, new GcSummary(byGeneration, byReason, totalPauseMs, maxPauseMs, lastHeap, lastPromoted));
    }

    public static async Task<IReadOnlyList<AllocationStat>> SampleAllocationsAsync(int pid, TimeSpan duration, CancellationToken cancellationToken)
    {
        Dictionary<string, (long Events, long Bytes)> byType = [];
        EventPipeProvider provider = new(ClrTraceEventParser.ProviderName, EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.GC);

        await RunSessionAsync(pid, [provider], duration, source =>
        {
            source.Clr.GCAllocationTick += (GCAllocationTickTraceData data) =>
            {
                string type = string.IsNullOrEmpty(data.TypeName) ? "?" : data.TypeName;
                (long events, long bytes) = byType.GetValueOrDefault(type);
                byType[type] = (events + 1, bytes + data.AllocationAmount64);
            };
        }, cancellationToken);

        List<AllocationStat> stats = [];
        foreach ((string type, (long events, long bytes)) in byType)
            stats.Add(new AllocationStat(type, events, bytes));
        stats.Sort(static (a, b) => b.SampledBytes.CompareTo(a.SampledBytes));
        return stats;
    }

    private static string? Field(IDictionary<string, object> payload, string name) =>
        payload.TryGetValue(name, out object? value) ? value?.ToString() : null;

    private static async Task RunSessionAsync(int pid, IReadOnlyList<EventPipeProvider> providers, TimeSpan duration, Action<EventPipeEventSource> subscribe, CancellationToken cancellationToken)
    {
        DiagnosticsClient client = new(pid);
        using EventPipeSession session = await client.StartEventPipeSessionAsync(providers, false, token: cancellationToken);
        using EventPipeEventSource source = new(session.EventStream);
        subscribe(source);

        Task processing = Task.Run(source.Process, CancellationToken.None);
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
            await processing;
        }
    }
}
