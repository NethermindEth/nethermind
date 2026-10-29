// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Core.Memory;

public static class Metrics
{
    [CounterMetric]
    [Description("Number of ordinary post-block collections the GC keeper ran after engine_newPayload.")]
    public static long GcKeeperPostBlockCollections;

    [CounterMetric]
    [Description("Number of post-block collections the GC keeper scheduled that the next engine_newPayload, or shutdown, called off before they ran.")]
    public static long GcKeeperPostBlockCancelled;

    [CounterMetric]
    [Description("Number of post-block collections the GC keeper held back because the previous one ran less than the minimum spacing before.")]
    public static long GcKeeperPostBlockThrottled;

    [CounterMetric]
    [Description("Number of post-block collections the GC keeper skipped because a no-GC region was active, an engine_newPayload was still in processing, or the GC scheduler declined (another collection running, or forced collections excluded).")]
    public static long GcKeeperPostBlockSkipped;

    [CounterMetric]
    [Description("Number of decommit collections (full, compacting, releasing memory to the operating system) the GC keeper ran.")]
    public static long GcKeeperDecommits;

    [CounterMetric]
    [Description("Number of times an engine_newPayload cut short a due decommit's wait for an idle gap; the decommit stays due.")]
    public static long GcKeeperDecommitIdleWaitsCancelled;

    [CounterMetric]
    [Description("Number of throwaway no-GC regions entered and ended at once right after a decommit collection, to keep the region's budget committed for the next engine_newPayload.")]
    public static long NoGcRegionRecommits;

    [GaugeMetric]
    [Description("The delay, in milliseconds, the GC keeper last waited after an engine_newPayload before its post-block collection.")]
    public static long GcKeeperPostBlockDelayMs;

    [GaugeMetric]
    [Description("The minimum spacing, in milliseconds, between the GC keeper's collections when it last claimed one.")]
    public static long GcKeeperMinMsBetweenCollections;

    [GaugeMetric]
    [Description("The median idle time, in milliseconds, between the end of an engine_newPayload and the start of the next, over the last 16; -1 until there are 16.")]
    public static long GcKeeperMedianIdleGapMs = -1;

    [GaugeMetric]
    [Description("The median interval, in milliseconds, between the starts of consecutive engine_newPayload calls, over the last 16; -1 until there are 16.")]
    public static long GcKeeperMedianPayloadIntervalMs = -1;

    /// <summary>Gives every keeper metric its start value back.</summary>
    /// <remarks>
    /// For what a keeper other than the node's own published, such as the startup warm-up's, which drives real payloads
    /// before the RPC opens. Call it only while no keeper is handling payloads.
    /// </remarks>
    public static void ResetGcKeeper()
    {
        GcKeeperPostBlockCollections = 0;
        GcKeeperPostBlockCancelled = 0;
        GcKeeperPostBlockThrottled = 0;
        GcKeeperPostBlockSkipped = 0;
        GcKeeperDecommits = 0;
        GcKeeperDecommitIdleWaitsCancelled = 0;
        NoGcRegionRecommits = 0;
        GcKeeperPostBlockDelayMs = 0;
        GcKeeperMinMsBetweenCollections = 0;
        GcKeeperMedianIdleGapMs = -1;
        GcKeeperMedianPayloadIntervalMs = -1;
    }
}
