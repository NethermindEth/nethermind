// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Evm.Precompiles;

public class Metrics
{
    [DetailedMetricOnFlag]
    public static bool DetailedMetricsEnabled { get; set; }

    [CounterMetric]
    [Description("Number of precompile runs, by precompile, in every execution context including RPC and tracing. Excludes cache hits and precompiles that do not support caching.")]
    [KeyIsLabel("precompile")]
    public static NonBlocking.ConcurrentDictionary<string, long> PrecompileRuns { get; } = new();

    [CounterMetric]
    [DetailedMetric]
    [Description("Precompile result cache probes, by precompile and probe result (block_hit, surviving_hit, miss). Counted only with detailed metrics enabled, since every probe pays for it.")]
    [KeyIsLabel("precompile", "result")]
    public static NonBlocking.ConcurrentDictionary<(string, string), long> PrecompileCacheProbes { get; } = new();

    [CounterMetric]
    [Description("Results the per-block precompile result cache refused because the precompile's byte budget was exhausted, by precompile. Non-zero means a larger budget would hold more; zero means raising it changes nothing.")]
    [KeyIsLabel("precompile")]
    public static NonBlocking.ConcurrentDictionary<string, long> PrecompileCacheRejectedFull { get; } = new();

    [GaugeMetric]
    [Description("Accounted weight held by the per-block precompile result cache, by precompile, as it stood at the end of the last block.")]
    [KeyIsLabel("precompile")]
    public static NonBlocking.ConcurrentDictionary<string, long> PrecompileCacheUsedBytes { get; } = new();

    [GaugeMetric]
    [Description("Entries held by the per-block precompile result cache, by precompile, as they stood at the end of the last block.")]
    [KeyIsLabel("precompile")]
    public static NonBlocking.ConcurrentDictionary<string, long> PrecompileCacheEntries { get; } = new();

    [GaugeMetric]
    [Description("Weighted byte budget of one precompile's per-block cache partition. Shares are equal for now. A precompile without a partition reports nothing, so nothing is reported at all while precompile caching is disabled.")]
    [KeyIsLabel("precompile")]
    public static NonBlocking.ConcurrentDictionary<string, long> PrecompileCachePartitionMaxBytes { get; } = new();

    [GaugeMetric]
    [Description("Entries held by the surviving precompile result cache, which is shared by every precompile.")]
    public static long PrecompileCacheSurvivingEntries { get; set; }
}
