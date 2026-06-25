// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Synchronization.SnapSync;

internal sealed class SnapRangeProfiler
{
    private const string EnabledVariable = "NETHERMIND_SNAP_RANGE_PROFILE";
    private const string IntervalVariable = "NETHERMIND_SNAP_RANGE_PROFILE_INTERVAL_SECONDS";
    private const string SlowRangeVariable = "NETHERMIND_SNAP_RANGE_SLOW_MS";
    private const int DefaultIntervalSeconds = 60;
    private const int DefaultSlowRangeMilliseconds = 1_000;
    private const int AccountIndex = 0;
    private const int StorageIndex = 1;
    private const int KindCount = 2;

    private readonly ILogger _logger;
    // Report cadence is compared to Stopwatch.GetTimestamp(), while range durations are stored as TimeSpan ticks.
    private readonly long _intervalTicks;
    private readonly long _slowRangeTicks;
    private readonly long[] _ranges = new long[KindCount];
    private readonly long[] _entries = new long[KindCount];
    private readonly long[] _proofs = new long[KindCount];
    private readonly long[] _boundaryNodes = new long[KindCount];
    private readonly long[] _persistedProbes = new long[KindCount];
    private readonly long[] _persistedHits = new long[KindCount];
    private readonly long[] _fillTicks = new long[KindCount];
    private readonly long[] _bulkTicks = new long[KindCount];
    private readonly long[] _stitchTicks = new long[KindCount];
    private readonly long[] _commitTicks = new long[KindCount];
    private readonly long[] _totalTicks = new long[KindCount];
    private readonly long[] _slowRanges = new long[KindCount];
    private readonly long[] _exceptions = new long[KindCount];
    private readonly long[] _maxCommitTicks = new long[KindCount];
    private readonly long[] _maxTotalTicks = new long[KindCount];

    private long _nextReportTimestamp;

    private SnapRangeProfiler(ILogger logger, TimeSpan interval, TimeSpan slowRangeThreshold)
    {
        _logger = logger;
        _intervalTicks = ToStopwatchTicks(interval);
        _slowRangeTicks = slowRangeThreshold.Ticks;
        _nextReportTimestamp = Stopwatch.GetTimestamp() + _intervalTicks;
    }

    public static SnapRangeProfiler? Create(ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger<SnapRangeProfiler>();
        if (!ReadBool(EnabledVariable, logger))
        {
            return null;
        }

        int intervalSeconds = ReadPositiveInt32(IntervalVariable, DefaultIntervalSeconds, logger);
        int slowRangeMilliseconds = ReadPositiveInt32(SlowRangeVariable, DefaultSlowRangeMilliseconds, logger);
        SnapRangeProfiler profiler = new(
            logger,
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromMilliseconds(slowRangeMilliseconds));

        if (logger.IsInfo)
        {
            logger.Info(
                $"Snap range profiling enabled: interval={intervalSeconds}s slowRange={slowRangeMilliseconds}ms");
        }

        return profiler;
    }

    public void ReportRange(
        bool isStorage,
        AddRangeResult result,
        bool threw,
        int entries,
        int proofs,
        int boundaryNodes,
        int persistedProbes,
        int persistedHits,
        long fillTicks,
        long bulkTicks,
        long stitchTicks,
        long commitTicks,
        long totalTicks)
    {
        int index = isStorage ? StorageIndex : AccountIndex;
        Interlocked.Increment(ref _ranges[index]);
        Interlocked.Add(ref _entries[index], entries);
        Interlocked.Add(ref _proofs[index], proofs);
        Interlocked.Add(ref _boundaryNodes[index], boundaryNodes);
        Interlocked.Add(ref _persistedProbes[index], persistedProbes);
        Interlocked.Add(ref _persistedHits[index], persistedHits);
        Interlocked.Add(ref _fillTicks[index], fillTicks);
        Interlocked.Add(ref _bulkTicks[index], bulkTicks);
        Interlocked.Add(ref _stitchTicks[index], stitchTicks);
        Interlocked.Add(ref _commitTicks[index], commitTicks);
        Interlocked.Add(ref _totalTicks[index], totalTicks);
        UpdateMax(ref _maxCommitTicks[index], commitTicks);
        UpdateMax(ref _maxTotalTicks[index], totalTicks);

        if (threw)
        {
            Interlocked.Increment(ref _exceptions[index]);
        }

        if (totalTicks >= _slowRangeTicks)
        {
            Interlocked.Increment(ref _slowRanges[index]);
            if (_logger.IsInfo)
            {
                _logger.Info(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Snap range profile slow type={KindName(index)} result={result} threw={threw} entries={entries} proofs={proofs} boundaryNodes={boundaryNodes} persistedProbes={persistedProbes} persistedHitPct={Percent(persistedHits, persistedProbes):F1} fillMs={ToMilliseconds(fillTicks):F1} bulkMs={ToMilliseconds(bulkTicks):F1} stitchMs={ToMilliseconds(stitchTicks):F1} commitMs={ToMilliseconds(commitTicks):F1} totalMs={ToMilliseconds(totalTicks):F1}"));
            }
        }

        MaybeReport();
    }

    private void MaybeReport()
    {
        long now = Stopwatch.GetTimestamp();
        long due = Volatile.Read(ref _nextReportTimestamp);
        if (now < due ||
            Interlocked.CompareExchange(ref _nextReportTimestamp, now + _intervalTicks, due) != due)
        {
            return;
        }

        Report("cumulative");
    }

    private void Report(string reason)
    {
        for (int index = 0; index < KindCount; index++)
        {
            long ranges = Volatile.Read(ref _ranges[index]);
            if (ranges == 0)
            {
                continue;
            }

            long entries = Volatile.Read(ref _entries[index]);
            long proofs = Volatile.Read(ref _proofs[index]);
            long boundaryNodes = Volatile.Read(ref _boundaryNodes[index]);
            long persistedProbes = Volatile.Read(ref _persistedProbes[index]);
            long persistedHits = Volatile.Read(ref _persistedHits[index]);
            long fillTicks = Volatile.Read(ref _fillTicks[index]);
            long bulkTicks = Volatile.Read(ref _bulkTicks[index]);
            long stitchTicks = Volatile.Read(ref _stitchTicks[index]);
            long commitTicks = Volatile.Read(ref _commitTicks[index]);
            long totalTicks = Volatile.Read(ref _totalTicks[index]);
            long slowRanges = Volatile.Read(ref _slowRanges[index]);
            long exceptions = Volatile.Read(ref _exceptions[index]);
            long maxCommitTicks = Volatile.Read(ref _maxCommitTicks[index]);
            long maxTotalTicks = Volatile.Read(ref _maxTotalTicks[index]);

            if (_logger.IsInfo)
            {
                _logger.Info(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Snap range profile {reason} type={KindName(index)} ranges={ranges} entries={entries} avgEntries={Average(entries, ranges):F1} proofs={proofs} avgProofs={Average(proofs, ranges):F1} boundaryNodes={boundaryNodes} avgBoundaryNodes={Average(boundaryNodes, ranges):F1} persistedProbes={persistedProbes} avgPersistedProbes={Average(persistedProbes, ranges):F1} persistedHitPct={Percent(persistedHits, persistedProbes):F1} avgFillMs={AverageMilliseconds(fillTicks, ranges):F1} avgBulkMs={AverageMilliseconds(bulkTicks, ranges):F1} avgStitchMs={AverageMilliseconds(stitchTicks, ranges):F1} avgCommitMs={AverageMilliseconds(commitTicks, ranges):F1} avgTotalMs={AverageMilliseconds(totalTicks, ranges):F1} maxCommitMs={ToMilliseconds(maxCommitTicks):F1} maxTotalMs={ToMilliseconds(maxTotalTicks):F1} slowRanges={slowRanges} exceptions={exceptions}"));
            }
        }
    }

    private static bool ReadBool(string name, ILogger logger)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out bool parsed))
        {
            return parsed;
        }

        if (value is "1" or "yes" or "on")
        {
            return true;
        }

        if (value is "0" or "no" or "off")
        {
            return false;
        }

        if (logger.IsWarn)
        {
            logger.Warn($"{name} must be true/false, 1/0, yes/no, or on/off. Profiling is disabled.");
        }

        return false;
    }

    private static int ReadPositiveInt32(string name, int fallback, ILogger logger)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        if (logger.IsWarn)
        {
            logger.Warn($"{name} must be a positive integer. Using the default value.");
        }

        return fallback;
    }

    private static long ToStopwatchTicks(TimeSpan value) =>
        value <= TimeSpan.Zero ? 0 : checked((long)(value.TotalSeconds * Stopwatch.Frequency));

    private static void UpdateMax(ref long location, long candidate)
    {
        long current;
        do
        {
            current = Volatile.Read(ref location);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref location, candidate, current) != current);
    }

    private static string KindName(int index) => index == StorageIndex ? "storage" : "account";

    private static double ToMilliseconds(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;

    private static double Average(long value, long count) => count == 0 ? 0 : value / (double)count;

    private static double AverageMilliseconds(long ticks, long count) =>
        count == 0 ? 0 : ToMilliseconds(ticks) / count;

    private static double Percent(long value, long total) => total == 0 ? 0 : value * 100.0 / total;
}
