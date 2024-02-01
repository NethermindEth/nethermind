// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using RocksDbSharp;

namespace Nethermind.Db.Rocks.Statistics;

public partial class DbMetricsUpdater<T> : IDisposable where T : Options<T>
{
    private readonly string _dbName;
    private readonly Options<T> _dbOptions;
    private readonly RocksDb _db;
    private readonly IDbConfig _dbConfig;
    private readonly ILogger _logger;
    private readonly ColumnFamilyHandle? _columnFamilyHandle;
    private Timer? _timer;

    public DbMetricsUpdater(string dbName, Options<T> dbOptions, RocksDb db, ColumnFamilyHandle? cf, IDbConfig dbConfig, ILogger logger)
    {
        _dbName = dbName;
        _dbOptions = dbOptions;
        _db = db;
        _dbConfig = dbConfig;
        _columnFamilyHandle = cf;
        _logger = logger;
    }

    public void StartUpdating()
    {
        var offsetInSec = _dbConfig.StatsDumpPeriodSec * 1.1;

        _timer = new Timer(UpdateMetrics, null, TimeSpan.FromSeconds(offsetInSec), TimeSpan.FromSeconds(offsetInSec));
    }

    private void UpdateMetrics(object? state)
    {
        try
        {
            // It seems that currently there is no other option with .NET api to extract the compaction statistics than through the dumped string
            var compactionStatsString = "";
            if (_columnFamilyHandle is not null)
            {
                compactionStatsString = _db.GetProperty("rocksdb.stats", _columnFamilyHandle);
            }
            else
            {
                compactionStatsString = _db.GetProperty("rocksdb.stats");
            }
            ProcessCompactionStats(compactionStatsString);

            if (_dbConfig.EnableDbStatistics)
            {
                var dbStatsString = _dbOptions.GetStatisticsString();
                ProcessStatisticsString(dbStatsString);
                // Currently we don't extract any DB statistics but we can do it here
            }
        }
        catch (Exception exc)
        {
            _logger.Error($"Error when updating metrics for {_dbName} database.", exc);
            // Maybe we would like to stop the _timer here to avoid logging the same error all over again?
        }
    }

    public void ProcessStatisticsString(string dbStatsString)
    {
        foreach ((string Name, IDictionary<string, double> SubMetric) value in ExtractStatsFromStatisticString(dbStatsString))
        {
            // The metric can be of several type, usually just a counter, but sometime its a histogram,
            // in which case we take both the sum and count.
            if (value.SubMetric.TryGetValue("SUM", out var valueSum))
            {
                Metrics.DbStats[($"{_dbName}Db", $"{value.Name}.sum")] = valueSum;
                Metrics.DbStats[($"{_dbName}Db", $"{value.Name}.count")] = value.SubMetric["COUNT"];
            }
            else
            {
                Metrics.DbStats[($"{_dbName}Db", value.Name)] = value.SubMetric["COUNT"];
            }
        }
    }

    private static IEnumerable<(string Name, IDictionary<string, double> Value)> ExtractStatsFromStatisticString(string statsStr)
    {
        var matches = ExtractStatsRegex2().Matches(statsStr);

        foreach (Match match in matches)
        {
            string statName = match.Groups[1].Value;

            IDictionary<string, double> metricDictionary = new Dictionary<string, double>();
            foreach (Match metricMatch in ExtractSubStatsRegex().Matches(match.Groups[2].Value))
            {
                metricDictionary[metricMatch.Groups[1].Value] = double.Parse(metricMatch.Groups[2].Value);
            }

            yield return (statName, metricDictionary);
        }
    }

    public void ProcessCompactionStats(string compactionStatsString)
    {
        if (!string.IsNullOrEmpty(compactionStatsString))
        {
            var stats = ExtractStatsPerLevel(compactionStatsString);
            UpdateMetricsFromList(stats);

            stats = ExctractIntervalCompaction(compactionStatsString);
            UpdateMetricsFromList(stats);
        }
        else
        {
            _logger.Warn($"No RocksDB compaction stats available for {_dbName} database.");
        }
    }

    private void UpdateMetricsFromList(List<(string Name, long Value)> stats)
    {
        if (stats is not null)
        {
            foreach (var stat in stats)
            {
                Metrics.DbStats[($"{_dbName}Db", stat.Name)] = stat.Value;
            }
        }
    }

    /// <summary>
    /// Example line:
    ///   L0      2/0    1.77 MB   0.5      0.0     0.0      0.0       0.4      0.4       0.0   1.0      0.0     44.6      9.83              0.00       386    0.025       0      0
    /// </summary>
    private static List<(string Name, long Value)> ExtractStatsPerLevel(string compactionStatsDump)
    {
        var stats = new List<(string Name, long Value)>(5);

        if (!string.IsNullOrEmpty(compactionStatsDump))
        {
            var rgx = ExtractStatsRegex();
            var matches = rgx.Matches(compactionStatsDump);

            foreach (Match m in matches)
            {
                var level = int.Parse(m.Groups[1].Value);
                stats.Add(($"Level{level}Files", int.Parse(m.Groups[2].Value)));
                stats.Add(($"Level{level}FilesCompacted", int.Parse(m.Groups[3].Value)));
            }
        }

        return stats;
    }

    /// <summary>
    /// Example line:
    /// Interval compaction: 0.00 GB write, 0.00 MB/s write, 0.00 GB read, 0.00 MB/s read, 0.0 seconds
    /// </summary>
    private List<(string Name, long Value)> ExctractIntervalCompaction(string compactionStatsDump)
    {
        var stats = new List<(string Name, long Value)>(5);

        if (!string.IsNullOrEmpty(compactionStatsDump))
        {
            var rgx = ExtractIntervalRegex();
            var match = rgx.Match(compactionStatsDump);

            if (match is not null && match.Success)
            {
                stats.Add(("IntervalCompactionGBWrite", long.Parse(match.Groups[1].Value)));
                stats.Add(("IntervalCompactionMBPerSecWrite", long.Parse(match.Groups[2].Value)));
                stats.Add(("IntervalCompactionGBRead", long.Parse(match.Groups[3].Value)));
                stats.Add(("IntervalCompactionMBPerSecRead", long.Parse(match.Groups[4].Value)));
                stats.Add(("IntervalCompactionSeconds", long.Parse(match.Groups[5].Value)));
            }
            else
            {
                _logger.Warn($"Cannot find 'Interval compaction' stats for {_dbName} database in the compation stats dump:{Environment.NewLine}{compactionStatsDump}");
            }
        }

        return stats;
    }

    [GeneratedRegex("^\\s+L(\\d+)\\s+(\\d+)\\/(\\d+).*$", RegexOptions.Multiline)]
    private static partial Regex ExtractStatsRegex();

    [GeneratedRegex("^Interval compaction: (\\d+)\\.\\d+.*GB write.*\\s+(\\d+)\\.\\d+.*MB\\/s write.*\\s+(\\d+)\\.\\d+.*GB read.*\\s+(\\d+)\\.\\d+.*MB\\/s read.*\\s+(\\d+)\\.\\d+.*seconds.*$", RegexOptions.Multiline)]
    private static partial Regex ExtractIntervalRegex();

    [GeneratedRegex("^(\\S+)(.*)$", RegexOptions.Multiline)]
    private static partial Regex ExtractStatsRegex2();

    [GeneratedRegex("(\\S+) \\: (\\S+)", RegexOptions.Multiline)]
    private static partial Regex ExtractSubStatsRegex();

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
