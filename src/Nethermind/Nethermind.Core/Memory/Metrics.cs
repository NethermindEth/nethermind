// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Core.Memory;

public static class Metrics
{
    [CounterMetric]
    [Description("Number of engine_newPayload calls that entered a no-GC region of their own.")]
    public static long NoGcRegionEntries;

    [CounterMetric]
    [Description("Number of engine_newPayload calls processed without a no-GC region: skipped by the guard or with the region set to Never, or whose entry the runtime declined or had not made when the payload ended.")]
    public static long NoGcRegionSkips;

    [CounterMetric]
    [Description("Number of engine_newPayload calls the guard let run without a no-GC region during which the runtime still ran a garbage collection.")]
    public static long NoGcRegionGuardMisses;

    [CounterMetric]
    [Description("Number of engine_newPayload calls during whose processing the runtime ran a garbage collection, region entries not counted.")]
    public static long NewPayloadsWithCollection;

    [CounterMetric]
    [Description("Number of throwaway no-GC regions entered and ended at once right after a decommit collection, to keep the region's budget committed for the next engine_newPayload; not counted in no_gc_region_entries.")]
    public static long NoGcRegionRecommits;

    [CounterMetric]
    [Description("Number of throwaway no-GC regions entered and ended at once right after an ordinary post-block collection on a quiet node with the region set to Guard, so that the next engine_newPayload finds the region's allocation budget armed and can skip its own entry; not counted in no_gc_region_entries or no_gc_region_recommits.")]
    public static long NoGcRegionRearms;

    [CounterMetric]
    [Description("Number of ordinary post-block collections with the region set to Guard after which the region's budget was not re-armed because the node allocated faster than 8 MB/s since the last engine_newPayload ended, or none had ended yet.")]
    public static long NoGcRegionRearmsSkippedBusy;

    [CounterMetric]
    [Description("Number of no-GC region re-arms that took more than 2 ms or during which the runtime collected, each pausing re-arms for the next 25 engine_newPayload calls.")]
    public static long NoGcRegionRearmBackoffs;
}
