// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Db;

public static partial class Metrics
{
    [GaugeMetric]
    [Description("Database reads per database")]
    [KeyIsLabel("db")]
    public static NonBlocking.ConcurrentDictionary<string, long> DbReads { get; } = new();

    [GaugeMetric]
    [Description("Database writes per database")]
    [KeyIsLabel("db")]
    public static NonBlocking.ConcurrentDictionary<string, long> DbWrites { get; } = new();

    [GaugeMetric]
    [Description("Database size per database")]
    [KeyIsLabel("db")]
    public static NonBlocking.ConcurrentDictionary<string, long> DbSize { get; } = new();

    [GaugeMetric]
    [Description("Database memtable per database")]
    [KeyIsLabel("db")]
    public static NonBlocking.ConcurrentDictionary<string, long> DbMemtableSize { get; } = new();

    [GaugeMetric]
    [Description("Database block cache size per database")]
    [KeyIsLabel("db")]
    public static NonBlocking.ConcurrentDictionary<string, long> DbBlockCacheSize { get; } = new();

    [GaugeMetric]
    [Description("Database index and filter size per database")]
    [KeyIsLabel("db")]
    public static NonBlocking.ConcurrentDictionary<string, long> DbIndexFilterSize { get; } = new();

    [Description("Metrics extracted from RocksDB Compaction Stats and DB Statistics")]
    [KeyIsLabel("db", "metric")]
    public static NonBlocking.ConcurrentDictionary<(string, string), double> DbStats { get; } = new();

    [Description("Metrics extracted from RocksDB Compaction Stats")]
    [KeyIsLabel("db", "level", "metric")]
    public static NonBlocking.ConcurrentDictionary<(string, int, string), double> DbCompactionStats { get; } = new();
}
