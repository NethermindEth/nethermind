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

    [GaugeMetric]
    [Description("Gen0 allocation budget left that the no-GC region guard requires to skip the region, at its last decision.")]
    public static long NoGcRegionGuardThresholdBytes { get; set; }

    [GaugeMetric]
    [Description("Estimated gen0 allocation budget left at the no-GC region guard's last decision; 0 when unknown.")]
    public static long NoGcRegionGuardBudgetLeftBytes { get; set; }

    [GaugeMetric]
    [Description("Gen0 allocation budget the no-GC region guard's last estimate started from; 0 when unknown.")]
    public static long NoGcRegionGuardGen0BudgetBytes { get; set; }

    [GaugeMetric]
    [Description("Most bytes allocated during one engine_newPayload over the last 300-600 payloads, as the no-GC region guard uses it (at least 64 MB).")]
    public static long NoGcRegionGuardBlockAllocationBytes { get; set; }
}
