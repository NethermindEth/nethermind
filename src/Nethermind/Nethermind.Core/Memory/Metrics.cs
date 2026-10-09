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
}
