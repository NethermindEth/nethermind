// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
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

    [CounterMetric]
    [Description("Number of throwaway no-GC regions entered (and ended at once) after a post-block collection to keep the region's budget committed; not counted in no_gc_region_entries.")]
    public static long NoGcRegionRecommits;

    // TEST BRANCH ONLY (bench/gc-guard-recommit), not for master: what each region entry costs. Times are wall-clock
    // milliseconds of the runtime call alone; maxima are monotonic since start, not reset on scrape.

    internal const string EntryContextAll = "all";
    internal const string EntryContextAfterDecommit = "after_decommit";
    internal const string EntryContextNotAfterDecommit = "not_after_decommit";
    internal const string EntryContextAfterSkip = "after_skip";
    internal const string EntryContextAfterEntry = "after_entry";
    private static readonly string[] _entryContexts =
        [EntryContextAll, EntryContextAfterDecommit, EntryContextNotAfterDecommit, EntryContextAfterSkip, EntryContextAfterEntry];
    private static readonly string[] _entryBuckets = ["lt_0.25", "0.25_0.5", "0.5_1", "1_2", "ge_2"];
    internal const string RecommitAfterDecommit = "decommit";
    internal const string RecommitAfterCollection = "collection";
    private static readonly string[] _recommitTriggers = [RecommitAfterDecommit, RecommitAfterCollection];

    internal static string EntryBucket(double ms) => ms switch
    {
        < 0.25 => _entryBuckets[0],
        < 0.5 => _entryBuckets[1],
        < 1 => _entryBuckets[2],
        < 2 => _entryBuckets[3],
        _ => _entryBuckets[4],
    };

    [KeyIsLabel("context")]
    [Description("TEST BRANCH: engine_newPayload no-GC region entries timed, by context: all; after_decommit (first entry after a decommit collection) vs not_after_decommit; after_skip (first entry after the guard skipped 1+ payloads) vs after_entry.")]
    public static ConcurrentDictionary<string, long> NoGcRegionEntryCount { get; } = Zeros<long>(_entryContexts);

    [KeyIsLabel("context")]
    [Description("TEST BRANCH: total milliseconds spent in the runtime's no-GC region entry for engine_newPayload, by context.")]
    public static ConcurrentDictionary<string, double> NoGcRegionEntryMsSum { get; } = Zeros<double>(_entryContexts);

    [KeyIsLabel("context")]
    [Description("TEST BRANCH: longest runtime no-GC region entry for engine_newPayload in milliseconds, by context; monotonic since start.")]
    public static ConcurrentDictionary<string, double> NoGcRegionEntryMsMax { get; } = Zeros<double>(_entryContexts);

    [KeyIsLabel("context", "bucket")]
    [Description("TEST BRANCH: engine_newPayload no-GC region entries by context and duration bucket in milliseconds (lt_0.25, 0.25_0.5, 0.5_1, 1_2, ge_2; not cumulative).")]
    public static ConcurrentDictionary<(string Context, string Bucket), long> NoGcRegionEntryMsBuckets { get; } = BucketZeros();

    [KeyIsLabel("after")]
    [Description("TEST BRANCH: throwaway re-commit regions entered and ended, by the collection they followed (decommit or collection).")]
    public static ConcurrentDictionary<string, long> NoGcRegionRecommitCount { get; } = Zeros<long>(_recommitTriggers);

    [KeyIsLabel("after")]
    [Description("TEST BRANCH: total milliseconds of the throwaway re-commit's runtime entry plus end, by the collection it followed.")]
    public static ConcurrentDictionary<string, double> NoGcRegionRecommitMsSum { get; } = Zeros<double>(_recommitTriggers);

    [KeyIsLabel("after")]
    [Description("TEST BRANCH: longest throwaway re-commit runtime entry plus end in milliseconds, by the collection it followed; monotonic since start.")]
    public static ConcurrentDictionary<string, double> NoGcRegionRecommitMsMax { get; } = Zeros<double>(_recommitTriggers);

    // Every series exists from start, so a rate over the first entry is not lost.
    private static ConcurrentDictionary<string, T> Zeros<T>(string[] keys) where T : struct
    {
        ConcurrentDictionary<string, T> zeros = new();
        foreach (string key in keys) zeros[key] = default;
        return zeros;
    }

    private static ConcurrentDictionary<(string Context, string Bucket), long> BucketZeros()
    {
        ConcurrentDictionary<(string Context, string Bucket), long> zeros = new();
        foreach (string context in _entryContexts)
        {
            foreach (string bucket in _entryBuckets) zeros[(context, bucket)] = 0;
        }
        return zeros;
    }
}
