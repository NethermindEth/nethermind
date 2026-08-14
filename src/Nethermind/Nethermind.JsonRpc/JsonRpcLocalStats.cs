// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.JsonRpc;

public class JsonRpcLocalStats : IJsonRpcLocalStats
{
    private readonly ITimestamper _timestamper;
    private readonly TimeSpan _reportingInterval;
    private readonly bool _enablePerMethodMetrics;
    private ConcurrentDictionary<string, MethodStats> _currentStats = new();
    private readonly ConcurrentDictionary<string, MethodStats> _allTimeStats = new();
    private DateTime _lastReport;
    private readonly ILogger _logger;
    private readonly Lock _reportRotationLock = new();

    public JsonRpcLocalStats(ITimestamper timestamper, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
    {
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        ArgumentNullException.ThrowIfNull(jsonRpcConfig);
        _reportingInterval = TimeSpan.FromSeconds(jsonRpcConfig.ReportIntervalSeconds);
        _enablePerMethodMetrics = jsonRpcConfig.EnablePerMethodMetrics;
        _logger = logManager?.GetClassLogger<JsonRpcLocalStats>() ?? throw new ArgumentNullException(nameof(logManager));
        _lastReport = _timestamper.UtcNow;
        IsEnabled = _enablePerMethodMetrics || _logger.IsInfo;
    }

    public bool IsEnabled { get; }

    public MethodStats GetMethodStats(string methodName) =>
        _allTimeStats.TryGetValue(methodName, out MethodStats? methodStats)
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

        if (!IsEnabled)
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
            Metrics.JsonRpcCallDurationMicros.Observe(reportHandlingTimeMicroseconds, label);
        }

        if (!_logger.IsInfo)
        {
            return;
        }

        ConcurrentDictionary<string, MethodStats>? statsForReport = RotateStatsForReport();

        MethodStats methodStats = _currentStats.GetOrAdd(report.Method, static _ => new MethodStats());
        MethodStats allTimeMethodStats = _allTimeStats.GetOrAdd(report.Method, static _ => new MethodStats());

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
            foreach (KeyValuePair<string, MethodStats> methodStats in stats.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                MethodStats snapshot = methodStats.Value.Snapshot();
                total.Successes += snapshot.Successes;
                total.Errors += snapshot.Errors;
                total.TotalTimeOfSuccessesMicros += snapshot.TotalTimeOfSuccessesMicros;
                total.TotalTimeOfErrorsMicros += snapshot.TotalTimeOfErrorsMicros;
                total.MaxTimeOfSuccess = Math.Max(total.MaxTimeOfSuccess, snapshot.MaxTimeOfSuccess);
                total.MaxTimeOfError = Math.Max(total.MaxTimeOfError, snapshot.MaxTimeOfError);
                total.TotalSizeBytes += snapshot.TotalSizeBytes;
                reportStringBuilder.AppendLine(PrepareReportLine(methodStats.Key, snapshot));
            }

            reportStringBuilder.AppendLine(_divider);
            reportStringBuilder.AppendLine(PrepareReportLine("TOTAL", total));
            reportStringBuilder.AppendLine(_divider);

            _logger.Info(reportStringBuilder.ToString());
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Error while building JSON RPC report.", ex);
        }
    }

    private sealed record ReportWorkItem(JsonRpcLocalStats Owner, ConcurrentDictionary<string, MethodStats> Stats);

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
