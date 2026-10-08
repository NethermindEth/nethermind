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
}
