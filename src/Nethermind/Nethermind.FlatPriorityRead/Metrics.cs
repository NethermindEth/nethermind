// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

namespace Nethermind.FlatPriorityRead;

/// <summary>Prometheus metrics for the flat priority read plugin.</summary>
public static class Metrics
{
    [GaugeMetric]
    [Description("Slots loaded into the flat priority read store at startup")]
    public static long FlatPriorityReadLoadedSlots { get; set; }

    [GaugeMetric]
    [Description("Milliseconds the startup load of the flat priority read store took")]
    public static long FlatPriorityReadLoadMilliseconds { get; set; }

    [DetailedMetric]
    [CounterMetric]
    [Description("Priority slot reads the store answered with a stored slot")]
    public static long FlatPriorityReadFound => _found.Sum;
    private static readonly StripedLong _found = new();
    internal static void IncrementFound() => _found.Increment();

    [DetailedMetric]
    [CounterMetric]
    [Description("Priority slot reads the store answered as absent")]
    public static long FlatPriorityReadNotFound => _notFound.Sum;
    private static readonly StripedLong _notFound = new();
    internal static void IncrementNotFound() => _notFound.Increment();
}
