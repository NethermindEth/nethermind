// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>Accumulates a <see cref="GcSummary"/> from the runtime's native GC events, live or from a trace file.</summary>
/// <remarks>
/// Pauses come from TraceEvent's GC analysis, which pairs each GC with its own suspension. Pairing suspend and restart events directly
/// miscounts: the runtime also suspends threads for other work, such as installing tiered-compilation call counters.
/// </remarks>
internal sealed class GcTracker
{
    private readonly Dictionary<int, int> _byGeneration = [];
    private readonly Dictionary<string, int> _byReason = [];
    private double _totalPauseMs;
    private double _maxPauseMs;
    private Dictionary<string, long>? _lastHeap;
    private long _lastPromoted;

    public void Subscribe(TraceEventDispatcher source)
    {
        source.NeedLoadedDotNetRuntimes();
        source.AddCallbackOnProcessStart(process => process.AddCallbackOnDotNetRuntimeLoad(runtime => runtime.GCEnd += (_, gc) => Add(gc)));
        source.Clr.GCHeapStats += data =>
        {
            _lastHeap = new Dictionary<string, long>
            {
                ["gen0"] = data.GenerationSize0,
                ["gen1"] = data.GenerationSize1,
                ["gen2"] = data.GenerationSize2,
                ["LOH"] = data.GenerationSize3,
                ["POH"] = data.GenerationSize4,
            };
            _lastPromoted = data.TotalPromotedSize0 + data.TotalPromotedSize1 + data.TotalPromotedSize2 + data.TotalPromotedSize3;
        };
    }

    public GcSummary ToSummary() => new(_byGeneration, _byReason, _totalPauseMs, _maxPauseMs, _lastHeap, _lastPromoted);

    private void Add(TraceGC gc)
    {
        _byGeneration[gc.Generation] = _byGeneration.GetValueOrDefault(gc.Generation) + 1;
        string reason = gc.Reason.ToString();
        _byReason[reason] = _byReason.GetValueOrDefault(reason) + 1;
        _totalPauseMs += gc.PauseDurationMSec;
        _maxPauseMs = Math.Max(_maxPauseMs, gc.PauseDurationMSec);
    }
}
