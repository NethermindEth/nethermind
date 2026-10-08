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
}
