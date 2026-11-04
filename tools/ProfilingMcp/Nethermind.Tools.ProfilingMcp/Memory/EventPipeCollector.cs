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

        GcTracker gc = new();

        await RunSessionAsync(pid, [counters, gcEvents], duration, source =>
        {
            gc.Subscribe(source);
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
        return (summaries, gc.ToSummary());
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
