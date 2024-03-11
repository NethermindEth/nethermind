// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using RocksDbSharp;

namespace Nethermind.Db.Rocks.Statistics;

public partial class DbMetricsUpdater<T>(string dbName, Options<T> dbOptions, RocksDb db, ColumnFamilyHandle? cf, IDbConfig dbConfig, ILogger logger)
    : IDisposable
    where T : Options<T>
{
    private Timer? _timer;

    public void StartUpdating()
    {
        var offsetInSec = dbConfig.StatsDumpPeriodSec * 1.1;

        _timer = new Timer(UpdateMetrics, null, TimeSpan.FromSeconds(offsetInSec), TimeSpan.FromSeconds(offsetInSec));
    }

    private void UpdateMetrics(object? state)
    {
        try
        {
            // It seems that currently there is no other option with .NET api to extract the compaction statistics than through the dumped string
            var compactionStatsString = "";
            compactionStatsString = cf is not null ? db.GetProperty("rocksdb.stats", cf) : db.GetProperty("rocksdb.stats");
            ProcessCompactionStats(compactionStatsString);

            if (dbConfig.EnableDbStatistics)
            {
                var dbStatsString = dbOptions.GetStatisticsString();
                ProcessStatisticsString(dbStatsString);
                // Currently we don't extract any DB statistics but we can do it here
            }
        }
        catch (Exception exc)
        {
            logger.Error($"Error when updating metrics for {dbName} database.", exc);
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
                Metrics.DbStats[($"{dbName}Db", $"{value.Name}.sum")] = valueSum;
                Metrics.DbStats[($"{dbName}Db", $"{value.Name}.count")] = value.SubMetric["COUNT"];
            }
            else
            {
                Metrics.DbStats[($"{dbName}Db", value.Name)] = value.SubMetric["COUNT"];
            }
        }
    }

    private static IEnumerable<(string Name, IDictionary<string, double> Value)> ExtractStatsFromStatisticString(string statsStr)
    {
        MatchCollection matches = ExtractStatsRegex2().Matches(statsStr);

        foreach (Match match in matches)
        {
            string statName = match.Groups["name"].Value;

            IDictionary<string, double> metricDictionary = new Dictionary<string, double>();
            foreach (Match metricMatch in ExtractSubStatsRegex().Matches(match.Groups["value"].Value))
            {
                metricDictionary[metricMatch.Groups["subName"].Value] = double.Parse(metricMatch.Groups["subValue"].Value);
            }

            yield return (statName, metricDictionary);
        }
    }

    public void ProcessCompactionStats(string compactionStatsString)
    {
        if (!string.IsNullOrEmpty(compactionStatsString))
        {
            ExtractStatsPerLevel(compactionStatsString);
            ExctractIntervalCompaction(compactionStatsString);
        }
        else
        {
            logger.Warn($"No RocksDB compaction stats available for {dbName} database.");
        }
    }

    // Level    Files   Size     Score Read(GB)  Rn(GB) Rnp1(GB) Write(GB) Wnew(GB) Moved(GB) W-Amp Rd(MB/s) Wr(MB/s) Comp(sec) CompMergeCPU(sec) Comp(cnt) Avg(sec) KeyIn KeyDrop Rblob(GB) Wblob(GB)
    //----------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //L0      2/0    1.77 MB   0.5      0.0     0.0      0.0       0.4      0.4       0.0   1.0      0.0     44.6      9.83              0.00       386    0.025       0      0
    [GeneratedRegex("^\\s+L(?<level>\\d+)\\s+(?<files>\\d+)\\/(?<files_compacting>\\d+)\\s+(?<size>\\S+)\\s+(?<size_scale>\\S+)\\s+(?<score>\\S+)\\s+(?<read>\\S+)\\s+(?<rn>\\S+)\\s+(?<rnp1>\\S+)\\s+(?<write>\\S+)\\s+(?<wnew>\\S+)\\s+(?<moved>\\S+)\\s+(?<wamp>\\S+)\\s+(?<rd>\\S+)\\s+(?<wr>\\S+)\\s+(?<comp_sec>\\S+)\\s+(?<comp_merge_cpu_sec>\\S+)\\s+(?<comp_total>\\S+)\\s+(?<comp_count>\\S+)\\s+(\\S+)\\s+(\\S+).*$",
        RegexOptions.Multiline | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex ExtractStatsRegex();

    private void ExtractStatsPerLevel(string compactionStatsDump)
    {
        if (!string.IsNullOrEmpty(compactionStatsDump))
        {
            Regex rgx = ExtractStatsRegex();
            MatchCollection matches = rgx.Matches(compactionStatsDump);

            foreach (Match m in matches)
            {
                int level = int.Parse(m.Groups["level"].Value);
                for (int i = 2; i <= 19; i++)
                {
                    Group group = m.Groups[i];
                    Metrics.DbCompactionStats[($"{dbName}Db", level, group.Name)] = double.Parse(group.Value) * i switch
                    {
                        4 => m.Groups[++i].Value switch
                        {
                            "KB" => 1.KiB(),
                            "MB" => 1.MiB(),
                            "GB" => 1.GiB(),
                            _ => 1
                        },
                        _ => 1
                    };
                }
            }
        }
    }

    /// <summary>
    /// Example line:
    /// Interval compaction: 0.00 GB write, 0.00 MB/s write, 0.00 GB read, 0.00 MB/s read, 0.0 seconds
    /// </summary>
    private void ExctractIntervalCompaction(string compactionStatsDump)
    {
        if (!string.IsNullOrEmpty(compactionStatsDump))
        {
            Regex rgx = ExtractIntervalRegex();
            Match match = rgx.Match(compactionStatsDump);

            if (match?.Success == true)
            {
                for (var index = 1; index < match.Groups.Count; index++)
                {
                    Group group = match.Groups[index];
                    Metrics.DbStats[($"{dbName}Db", group.Name)] = long.Parse(group.Value);
                }
            }
            else
            {
                logger.Warn($"Cannot find 'Interval compaction' stats for {dbName} database in the compation stats dump:{Environment.NewLine}{compactionStatsDump}");
            }
        }
    }

    [GeneratedRegex("^Interval compaction: (?<IntervalCompactionGBWrite>\\d+)\\.\\d+.*GB write.*\\s+(?<IntervalCompactionMBPerSecWrite>\\d+)\\.\\d+.*MB\\/s write.*\\s+(?<IntervalCompactionGBRead>\\d+)\\.\\d+.*GB read.*\\s+(?<IntervalCompactionMBPerSecRead>\\d+)\\.\\d+.*MB\\/s read.*\\s+(?<IntervalCompactionSeconds>\\d+)\\.\\d+.*seconds.*$",
        RegexOptions.Multiline | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex ExtractIntervalRegex();

    [GeneratedRegex("^(?<name>\\S+)(?<value>.*)$", RegexOptions.Multiline | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex ExtractStatsRegex2();

    [GeneratedRegex("(?<subName>\\S+) \\: (?<subValue>\\S+)", RegexOptions.Singleline | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex ExtractSubStatsRegex();

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
