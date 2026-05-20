// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    private ConcurrentDictionary<string, MethodStats> _currentStats = new();
    private readonly ConcurrentDictionary<string, MethodStats> _allTimeStats = new();
    private DateTime _lastReport = timestamper.UtcNow;
    private readonly ILogger _logger = logManager?.GetClassLogger<JsonRpcLocalStats>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly Lock _reportRotationLock = new();

    public MethodStats GetMethodStats(string methodName) => _allTimeStats.GetValueOrDefault(methodName, new MethodStats());

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

        long startTimestamp = _enablePerMethodMetrics ? Stopwatch.GetTimestamp() : 0;
        try
        {
            ReportCallInternal(report, elapsedMicroseconds, size);
        }
        finally
        {
            if (_enablePerMethodMetrics)
            {
                Metrics.JsonRpcLocalStatsLatencyMicros.Observe(
                    (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds,
                    new JsonRpcMetricLabels(report.Method, report.Success));
            }
        }
    }

    private void ReportCallInternal(RpcReport report, long elapsedMicroseconds, long? size)
    {
        long reportHandlingTimeMicroseconds = elapsedMicroseconds == 0 ? report.HandlingTimeMicroseconds : elapsedMicroseconds;

        if (_enablePerMethodMetrics)
        {
            JsonRpcMetricLabels label = new(report.Method, report.Success);
            Metrics.JsonRpcCallLatencyMicros.Observe(reportHandlingTimeMicroseconds, label);
            ObserveBoundaryTimings(report.BoundaryTimings, label);
        }

        if (!_logger.IsInfo)
        {
            return;
        }

        ConcurrentDictionary<string, MethodStats>? statsForReport = RotateStatsForReport();

        MethodStats methodStats = _currentStats.GetOrAdd(report.Method, static _ => new MethodStats());
        MethodStats allTimeMethodStats = _allTimeStats.GetOrAdd(report.Method, static _ => new MethodStats());

        decimal sizeDec = size ?? 0;

        lock (methodStats)
        {
            if (report.Success)
            {
                methodStats.AvgTimeOfSuccesses =
                    (methodStats.Successes * methodStats.AvgTimeOfSuccesses + reportHandlingTimeMicroseconds) /
                    ++methodStats.Successes;
                methodStats.MaxTimeOfSuccess =
                    Math.Max(methodStats.MaxTimeOfSuccess, reportHandlingTimeMicroseconds);

                allTimeMethodStats.AvgTimeOfSuccesses =
                    (allTimeMethodStats.Successes * allTimeMethodStats.AvgTimeOfSuccesses +
                        reportHandlingTimeMicroseconds) /
                    ++allTimeMethodStats.Successes;
                allTimeMethodStats.MaxTimeOfSuccess =
                    Math.Max(allTimeMethodStats.MaxTimeOfSuccess, reportHandlingTimeMicroseconds);
            }
            else
            {
                methodStats.AvgTimeOfErrors =
                    (methodStats.Errors * methodStats.AvgTimeOfErrors + reportHandlingTimeMicroseconds) /
                    ++methodStats.Errors;
                methodStats.MaxTimeOfError = Math.Max(methodStats.MaxTimeOfError, reportHandlingTimeMicroseconds);

                allTimeMethodStats.AvgTimeOfErrors =
                    (allTimeMethodStats.Errors * allTimeMethodStats.AvgTimeOfErrors + reportHandlingTimeMicroseconds) /
                    ++allTimeMethodStats.Errors;
                allTimeMethodStats.MaxTimeOfError = Math.Max(allTimeMethodStats.MaxTimeOfError, reportHandlingTimeMicroseconds);
            }

            methodStats.TotalSize += sizeDec;
            allTimeMethodStats.TotalSize += sizeDec;
        }

        if (statsForReport is not null)
        {
            QueueReport(statsForReport);
        }
    }

    private static void ObserveBoundaryTimings(RpcBoundaryTimings timings, JsonRpcMetricLabels label)
    {
        if (!timings.HasMeasurements)
        {
            return;
        }

        Metrics.JsonRpcBoundaryLatencyMicros.Observe(timings.BoundaryMicroseconds, label);
        long measuredMicroseconds = timings.BoundaryMicroseconds + timings.MethodBodyMicroseconds;
        if (measuredMicroseconds != 0)
        {
            Metrics.JsonRpcBoundaryLatencyPercent.Observe(
                (double)timings.BoundaryMicroseconds * 100.0 / measuredMicroseconds,
                label);
        }

        Metrics.JsonRpcPreMethodBoundaryLatencyMicros.Observe(timings.PreMethodMicroseconds, label);
        Metrics.JsonRpcRequestBodyCollectionLatencyMicros.Observe(timings.RequestBodyCollectionMicroseconds, label);
        Metrics.JsonRpcEnvelopeParseLatencyMicros.Observe(timings.EnvelopeParseMicroseconds, label);
        Metrics.JsonRpcMethodBodyLatencyMicros.Observe(timings.MethodBodyMicroseconds, label);
        Metrics.JsonRpcPostMethodBoundaryLatencyMicros.Observe(timings.PostMethodMicroseconds, label);
        Metrics.JsonRpcResponseWriteLatencyMicros.Observe(timings.ResponseWriteMicroseconds, label);
        Metrics.JsonRpcResponseFlushLatencyMicros.Observe(timings.ResponseFlushMicroseconds, label);
        Metrics.JsonRpcResponseFlushCount.Observe(timings.ResponseFlushCount, label);
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

    private ConcurrentDictionary<string, MethodStats>? RotateStatsForReport()
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

            ConcurrentDictionary<string, MethodStats> statsForReport = _currentStats;
            _currentStats = new ConcurrentDictionary<string, MethodStats>();

            return statsForReport.IsEmpty ? null : statsForReport;
        }
    }

    private void QueueReport(ConcurrentDictionary<string, MethodStats> statsForReport) =>
        ThreadPool.QueueUserWorkItem(static state =>
        {
            ReportWorkItem workItem = (ReportWorkItem)state!;
            workItem.Owner.BuildReport(workItem.Stats);
        }, new ReportWorkItem(this, statsForReport));

    private void BuildReport(ConcurrentDictionary<string, MethodStats> stats)
    {
        try
        {
            StringBuilder reportStringBuilder = new();

            reportStringBuilder.AppendLine("***** JSON RPC report *****");
            reportStringBuilder.AppendLine(_divider);
            reportStringBuilder.AppendLine(ReportHeader);
            reportStringBuilder.AppendLine(_divider);
            MethodStats total = new();
            foreach (KeyValuePair<string, MethodStats> methodStats in stats.OrderBy(static kv => kv.Key))
            {
                MethodStats snapshot = Snapshot(methodStats.Value);
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

    private static MethodStats Snapshot(MethodStats methodStats)
    {
        lock (methodStats)
        {
            return new MethodStats
            {
                Successes = methodStats.Successes,
                Errors = methodStats.Errors,
                AvgTimeOfErrors = methodStats.AvgTimeOfErrors,
                AvgTimeOfSuccesses = methodStats.AvgTimeOfSuccesses,
                MaxTimeOfError = methodStats.MaxTimeOfError,
                MaxTimeOfSuccess = methodStats.MaxTimeOfSuccess,
                TotalSize = methodStats.TotalSize
            };
        }
    }

    private sealed class ReportWorkItem(
        JsonRpcLocalStats owner,
        ConcurrentDictionary<string, MethodStats> stats)
    {
        public JsonRpcLocalStats Owner { get; } = owner;
        public ConcurrentDictionary<string, MethodStats> Stats { get; } = stats;
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
