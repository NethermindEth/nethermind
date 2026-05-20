// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.JsonRpc;

public class JsonRpcLocalStats(ITimestamper timestamper, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
    : IJsonRpcLocalStats
{
    private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    private readonly TimeSpan _reportingInterval = TimeSpan.FromSeconds(jsonRpcConfig.ReportIntervalSeconds);
    private readonly bool _enablePerMethodMetrics = jsonRpcConfig.EnablePerMethodMetrics;
    private ConcurrentDictionary<string, AtomicMethodStats> _currentStats = new();
    private readonly ConcurrentDictionary<string, AtomicMethodStats> _allTimeStats = new();
    private DateTime _lastReport = timestamper.UtcNow;
    private readonly ILogger _logger = logManager?.GetClassLogger<JsonRpcLocalStats>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly Lock _reportRotationLock = new();

    public MethodStats GetMethodStats(string methodName) =>
        _allTimeStats.TryGetValue(methodName, out AtomicMethodStats? methodStats)
            ? methodStats.Snapshot()
            : new MethodStats();

    public void ReportCall(string method, long handlingTimeMicroseconds, bool success) =>
        ReportCall(new RpcReport(method, handlingTimeMicroseconds, success));

    public void ReportCall(RpcReport report, long elapsedMicroseconds = 0, long? size = null)
    {
        if (string.IsNullOrWhiteSpace(report.Method))
        {
            return;
        }

        if (!_enablePerMethodMetrics && !_logger.IsInfo)
        {
            return;
        }

        ReportCallInternal(report, elapsedMicroseconds, size);
    }

    private void ReportCallInternal(RpcReport report, long elapsedMicroseconds, long? size)
    {
        long reportHandlingTimeMicroseconds = elapsedMicroseconds == 0 ? report.HandlingTimeMicroseconds : elapsedMicroseconds;

        if (_enablePerMethodMetrics)
        {
            JsonRpcMetricLabels label = new(report.Method, report.Success);
            Metrics.JsonRpcCallLatencyMicros.Observe(reportHandlingTimeMicroseconds, label);
        }

        if (!_logger.IsInfo)
        {
            return;
        }

        ConcurrentDictionary<string, AtomicMethodStats>? statsForReport = RotateStatsForReport();

        AtomicMethodStats methodStats = _currentStats.GetOrAdd(report.Method, static _ => new AtomicMethodStats());
        AtomicMethodStats allTimeMethodStats = _allTimeStats.GetOrAdd(report.Method, static _ => new AtomicMethodStats());

        long responseSize = size ?? 0;
        methodStats.Record(reportHandlingTimeMicroseconds, responseSize, report.Success);
        allTimeMethodStats.Record(reportHandlingTimeMicroseconds, responseSize, report.Success);

        if (statsForReport is not null)
        {
            QueueReport(statsForReport);
        }
    }

    private const string ReportHeader = "method                                  | " +
                                        "successes | " +
                                        "  avg (ms) | " +
                                        "  max (ms) | " +
                                        "   errors | " +
                                        "  avg (ms) | " +
                                        "  max (ms) |" +
                                        " avg size B |" +
                                        " total (kB) |";

    private static readonly string _divider = new('-', ReportHeader.Length);

    private ConcurrentDictionary<string, AtomicMethodStats>? RotateStatsForReport()
    {
        DateTime thisTime = _timestamper.UtcNow;
        if (thisTime - _lastReport <= _reportingInterval)
        {
            return null;
        }

        lock (_reportRotationLock)
        {
            if (thisTime - _lastReport <= _reportingInterval)
            {
                return null;
            }

            _lastReport = thisTime;

            ConcurrentDictionary<string, AtomicMethodStats> statsForReport = _currentStats;
            _currentStats = new ConcurrentDictionary<string, AtomicMethodStats>();

            return statsForReport.IsEmpty ? null : statsForReport;
        }
    }

    private void QueueReport(ConcurrentDictionary<string, AtomicMethodStats> statsForReport) =>
        ThreadPool.QueueUserWorkItem(static state =>
        {
            ReportWorkItem workItem = (ReportWorkItem)state!;
            workItem.Owner.BuildReport(workItem.Stats);
        }, new ReportWorkItem(this, statsForReport));

    private void BuildReport(ConcurrentDictionary<string, AtomicMethodStats> stats)
    {
        try
        {
            StringBuilder reportStringBuilder = new();

            reportStringBuilder.AppendLine("***** JSON RPC report *****");
            reportStringBuilder.AppendLine(_divider);
            reportStringBuilder.AppendLine(ReportHeader);
            reportStringBuilder.AppendLine(_divider);
            MethodStats total = new();
            foreach (KeyValuePair<string, AtomicMethodStats> methodStats in stats.OrderBy(static kv => kv.Key))
            {
                MethodStats snapshot = methodStats.Value.Snapshot();
                total.AvgTimeOfSuccesses = total.Successes + snapshot.Successes == 0
                    ? 0
                    : (total.AvgTimeOfSuccesses * total.Successes + snapshot.Successes * snapshot.AvgTimeOfSuccesses)
                      / (total.Successes + snapshot.Successes);
                total.AvgTimeOfErrors = total.Errors + snapshot.Errors == 0
                    ? 0
                    : (total.AvgTimeOfErrors * total.Errors + snapshot.Errors * snapshot.AvgTimeOfErrors)
                      / (total.Errors + snapshot.Errors);
                total.Successes += snapshot.Successes;
                total.Errors += snapshot.Errors;
                total.MaxTimeOfSuccess = Math.Max(total.MaxTimeOfSuccess, snapshot.MaxTimeOfSuccess);
                total.MaxTimeOfError = Math.Max(total.MaxTimeOfError, snapshot.MaxTimeOfError);
                total.TotalSize += snapshot.TotalSize;
                reportStringBuilder.AppendLine(PrepareReportLine(methodStats.Key, snapshot));
            }

            reportStringBuilder.AppendLine(_divider);
            reportStringBuilder.AppendLine(PrepareReportLine("TOTAL", total));
            reportStringBuilder.AppendLine(_divider);

            lock (_logger.UnderlyingLogger)
            {
                _logger.Info(reportStringBuilder.ToString());
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Error while building JSON RPC report.", ex);
        }
    }

    private sealed class ReportWorkItem(
        JsonRpcLocalStats owner,
        ConcurrentDictionary<string, AtomicMethodStats> stats)
    {
        public JsonRpcLocalStats Owner { get; } = owner;
        public ConcurrentDictionary<string, AtomicMethodStats> Stats { get; } = stats;
    }

    private sealed class AtomicMethodStats
    {
        private long _successes;
        private long _errors;
        private long _totalSuccessMicroseconds;
        private long _totalErrorMicroseconds;
        private long _maxSuccessMicroseconds;
        private long _maxErrorMicroseconds;
        private long _totalSize;

        public void Record(long handlingTimeMicroseconds, long size, bool success)
        {
            if (success)
            {
                Interlocked.Increment(ref _successes);
                Interlocked.Add(ref _totalSuccessMicroseconds, handlingTimeMicroseconds);
                SetMax(ref _maxSuccessMicroseconds, handlingTimeMicroseconds);
            }
            else
            {
                Interlocked.Increment(ref _errors);
                Interlocked.Add(ref _totalErrorMicroseconds, handlingTimeMicroseconds);
                SetMax(ref _maxErrorMicroseconds, handlingTimeMicroseconds);
            }

            Interlocked.Add(ref _totalSize, size);
        }

        public MethodStats Snapshot()
        {
            long successes = Volatile.Read(ref _successes);
            long errors = Volatile.Read(ref _errors);
            long totalSuccessMicroseconds = Volatile.Read(ref _totalSuccessMicroseconds);
            long totalErrorMicroseconds = Volatile.Read(ref _totalErrorMicroseconds);

            return new MethodStats
            {
                Successes = ToInt32Count(successes),
                Errors = ToInt32Count(errors),
                AvgTimeOfSuccesses = successes == 0 ? 0 : (decimal)totalSuccessMicroseconds / successes,
                AvgTimeOfErrors = errors == 0 ? 0 : (decimal)totalErrorMicroseconds / errors,
                MaxTimeOfSuccess = Volatile.Read(ref _maxSuccessMicroseconds),
                MaxTimeOfError = Volatile.Read(ref _maxErrorMicroseconds),
                TotalSize = Volatile.Read(ref _totalSize)
            };
        }

        private static void SetMax(ref long target, long value)
        {
            long current = Volatile.Read(ref target);
            while (value > current)
            {
                long previous = Interlocked.CompareExchange(ref target, value, current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }

        private static int ToInt32Count(long value) =>
            value > int.MaxValue ? int.MaxValue : (int)value;
    }

    [Pure]
    private static string PrepareReportLine(in string key, MethodStats methodStats) =>
        $"{key,-40}| " +
        $"{methodStats.Successes,9} | " +
        $"{((double)methodStats.AvgTimeOfSuccesses / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),10} | " +
        $"{((double)methodStats.MaxTimeOfSuccess / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),10} | " +
        $"{methodStats.Errors,9} | " +
        $"{((double)methodStats.AvgTimeOfErrors / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),10} | " +
        $"{((double)methodStats.MaxTimeOfError / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),10} | " +
        $"{methodStats.AvgSize.ToString("0", CultureInfo.InvariantCulture),10} | " +
        $"{((double)methodStats.TotalSize / 1024.0).ToString("0.00", CultureInfo.InvariantCulture),10} | ";
}
