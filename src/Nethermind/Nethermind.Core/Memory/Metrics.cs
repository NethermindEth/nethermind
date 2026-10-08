// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Core.Memory;

public static class Metrics
{
    [CounterMetric]
    [Description("Number of no-GC regions entered after engine_getBlobs, ahead of the engine_newPayload they are kept for.")]
    public static long NoGcRegionPreEntries;

    [CounterMetric]
    [Description("Number of no-GC regions entered after engine_getBlobs that engine_newPayload took over.")]
    public static long NoGcRegionPreEntriesTakenOver;

    [CounterMetric]
    [Description("Number of no-GC regions entered after engine_getBlobs that expired without engine_newPayload taking them over.")]
    public static long NoGcRegionPreEntriesExpired;

    [CounterMetric]
    [Description("Number of no-GC regions entered after engine_getBlobs that engine_newPayload found too old or too depleted and ended.")]
    public static long NoGcRegionPreEntriesStale;

    [CounterMetric]
    [Description("Number of no-GC regions after engine_getBlobs whose entry failed.")]
    public static long NoGcRegionPreEntriesFailed;

    [CounterMetric]
    [Description("Number of no-GC regions the runtime ended before the engine_newPayload holding them was done (budget spent mid-block).")]
    public static long NoGcRegionEndedByRuntime;

    [CounterMetric]
    [Description("Number of no-GC regions entered ahead of the slot (part of NoGcRegionPreEntries).")]
    public static long NoGcRegionPreSlotEntries;

    [CounterMetric]
    [Description("Number of no-GC regions entered ahead of the slot that engine_newPayload took over.")]
    public static long NoGcRegionPreSlotEntriesTakenOver;

    [CounterMetric]
    [Description("Number of no-GC regions entered ahead of the slot that expired without engine_newPayload taking them over.")]
    public static long NoGcRegionPreSlotEntriesExpired;

    [CounterMetric]
    [Description("Number of no-GC regions entered ahead of the slot that engine_newPayload found too old or too depleted and ended.")]
    public static long NoGcRegionPreSlotEntriesStale;

    [CounterMetric]
    [Description("Number of no-GC regions ahead of the slot whose entry failed.")]
    public static long NoGcRegionPreSlotEntriesFailed;

    [CounterMetric]
    [Description("Bytes allocated by the process between the entry of pre-entered no-GC regions and engine_newPayload taking them over (sum).")]
    public static long NoGcRegionPreEntryAllocatedBytesAtTakeover;

    [CounterMetric]
    [Description("Bytes allocated by the process between the entry of slot pre-entered no-GC regions and engine_newPayload taking them over (sum).")]
    public static long NoGcRegionPreSlotAllocatedBytesAtTakeover;

    [GaugeMetric]
    [Description("Most bytes allocated by the process between the entry of a pre-entered no-GC region and engine_newPayload taking it over.")]
    public static long NoGcRegionPreEntryAllocatedBytesAtTakeoverMax;

    [GaugeMetric]
    [Description("Most bytes allocated by the process between the entry of a slot pre-entered no-GC region and engine_newPayload taking it over.")]
    public static long NoGcRegionPreSlotAllocatedBytesAtTakeoverMax;

    [CounterMetric]
    [Description("Bytes allocated by the process between the entry of pre-entered no-GC regions and engine_newPayload finding them stale (sum).")]
    public static long NoGcRegionPreEntryAllocatedBytesAtStale;

    [CounterMetric]
    [Description("Bytes allocated by the process between the entry of slot pre-entered no-GC regions and engine_newPayload finding them stale (sum).")]
    public static long NoGcRegionPreSlotAllocatedBytesAtStale;

    [CounterMetric]
    [Description("Number of engine_newPayload calls that queued a no-GC region entry of their own (not taken over or shared).")]
    public static long NoGcRegionPayloadEntries;

    [CounterMetric]
    [Description("Number of engine_newPayload calls that entered no region (BENCH_GC_REGION_ENTRY never, or the guard found the gen0 budget covering the block); their post-block collection still runs.")]
    public static long NoGcRegionPayloadSkips;

    [CounterMetric]
    [Description("Number of pre-entries (getBlobs or slot) not made because the guard found the gen0 budget covering the block.")]
    public static long NoGcRegionPreEntriesSkippedByGuard;

    [CounterMetric]
    [Description("Number of engine_newPayload calls whose estimated gen0 budget left was at least BENCH_GC_REGION_GUARD_MB (guard and never modes).")]
    public static long NoGcRegionGuardCovered;

    [CounterMetric]
    [Description("Number of engine_newPayload calls judged covered by the gen0 budget that still had a gen0 collection during processing.")]
    public static long NoGcRegionGuardMisses;

    [GaugeMetric]
    [Description("Estimated gen0 allocation budget left at the last engine_newPayload or pre-entry (guard and never modes; -1 unknown).")]
    public static long NoGcRegionGuardBudgetLeftBytes;

    [GaugeMetric]
    [Description("Gen0 allocation budget summed over heaps at the last estimate (runtime or BENCH_GC_REGION_GUARD_BUDGET_MB; -1 unknown).")]
    public static long NoGcRegionGuardGen0BudgetBytes;

    [CounterMetric]
    [Description("Number of engine_newPayload calls whose processing window (start to the region's end, or the same point without a region) was measured.")]
    public static long NoGcRegionPayloadsMeasured;

    [CounterMetric]
    [Description("Number of engine_newPayload calls with at least one gen0 (or higher) collection during processing, own region entries excluded.")]
    public static long NoGcRegionPayloadsWithCollectionInProcessing;

    [CounterMetric]
    [Description("Collections counted by GC.CollectionCount(0) during engine_newPayload processing (gen0 and higher), own region entries excluded.")]
    public static long NoGcRegionGen0CollectionsInProcessing;

    [CounterMetric]
    [Description("Collections counted by GC.CollectionCount(1) during engine_newPayload processing (gen1 and gen2), own region entries excluded.")]
    public static long NoGcRegionGen1CollectionsInProcessing;

    [CounterMetric]
    [Description("Collections counted by GC.CollectionCount(2) during engine_newPayload processing (gen2, incl. background), own region entries excluded.")]
    public static long NoGcRegionGen2CollectionsInProcessing;

    [CounterMetric]
    [Description("Bytes allocated by the process during engine_newPayload processing windows (sum).")]
    public static long NoGcRegionPayloadAllocatedBytes;

    [GaugeMetric]
    [Description("Most bytes allocated by the process during one engine_newPayload processing window.")]
    public static long NoGcRegionPayloadAllocatedBytesMax;

    [CounterMetric]
    [Description("Number of throwaway no-GC regions entered and ended right after the aggressive decommit collection, to re-commit the next entry's budget off the payload's path.")]
    public static long NoGcRegionRecommits;

    [CounterMetric]
    [Description("Number of re-commits after the aggressive decommit collection that were cancelled, skipped or refused.")]
    public static long NoGcRegionRecommitsSkipped;
}
