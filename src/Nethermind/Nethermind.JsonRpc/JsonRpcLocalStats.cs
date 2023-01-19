// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.JsonRpc
{
    public class JsonRpcLocalStats : IJsonRpcLocalStats
    {
        private readonly ITimestamper _timestamper;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly TimeSpan _reportingInterval;

        private ConcurrentDictionary<string, MethodStats> _currentStats = new();
        private ConcurrentDictionary<string, MethodStats> _previousStats = new();
        private readonly ConcurrentDictionary<string, MethodStats> _allTimeStats = new();
        private DateTime _lastReport = DateTime.MinValue;
        private readonly ILogger _logger;

        public JsonRpcLocalStats(ITimestamper timestamper, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
        {
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _reportingInterval = TimeSpan.FromSeconds(jsonRpcConfig.ReportIntervalSeconds);
        }

        public MethodStats GetMethodStats(string methodName) => _allTimeStats.GetValueOrDefault(methodName, new MethodStats());

        public void ReportCall(string method, long handlingTimeMicroseconds, bool success) =>
            ReportCall(new RpcReport(method, handlingTimeMicroseconds, success));

        public void ReportCall(in RpcReport report, long elapsedMicroseconds = 0, long? size = null)
        {
            if (string.IsNullOrWhiteSpace(report.Method))
            {
                return;
            }

            DateTime thisTime = _timestamper.UtcNow;
            if (thisTime - _lastReport > _reportingInterval)
            {
                _lastReport = thisTime;
                BuildReport();
            }

            _currentStats.TryGetValue(report.Method, out MethodStats methodStats);
            _allTimeStats.TryGetValue(report.Method, out MethodStats allTimeMethodStats);
            methodStats ??= _currentStats.GetOrAdd(report.Method, _ => new MethodStats());
            allTimeMethodStats ??= _allTimeStats.GetOrAdd(report.Method, _ => new MethodStats());

            long reportHandlingTimeMicroseconds = elapsedMicroseconds == 0 ? report.HandlingTimeMicroseconds : elapsedMicroseconds;

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
        }

        private void BuildReport()
        {
            if (!_logger.IsInfo)
            {
                return;
            }

            Swap();

            if (!_previousStats.Any())
            {
                return;
            }

            const string reportHeader = "method                                  | " +
                                        "successes | " +
                                        " avg time (µs) | " +
                                        " max time (µs) | " +
                                        "   errors | " +
                                        " avg time (µs) | " +
                                        " max time (µs) |" +
                                        " avg size |" +
                                        " total size |";

            StringBuilder stringBuilder = new();
            stringBuilder.AppendLine("***** JSON RPC report *****");
            string divider = new(Enumerable.Repeat('-', reportHeader.Length).ToArray());
            stringBuilder.AppendLine(divider);
            stringBuilder.AppendLine(reportHeader);
            stringBuilder.AppendLine(divider);
            MethodStats total = new();
            foreach (KeyValuePair<string, MethodStats> methodStats in _previousStats.OrderBy(kv => kv.Key))
            {
                total.AvgTimeOfSuccesses = total.Successes + methodStats.Value.Successes == 0
                    ? 0
                    : (total.AvgTimeOfSuccesses * total.Successes + methodStats.Value.Successes * methodStats.Value.AvgTimeOfSuccesses)
                      / (total.Successes + methodStats.Value.Successes);
                total.AvgTimeOfErrors = total.Errors + methodStats.Value.Errors == 0
                    ? 0
                    : (total.AvgTimeOfErrors * total.Errors + methodStats.Value.Errors * methodStats.Value.AvgTimeOfErrors)
                      / (total.Errors + methodStats.Value.Errors);
                total.Successes += methodStats.Value.Successes;
                total.Errors += methodStats.Value.Errors;
                total.MaxTimeOfSuccess = Math.Max(total.MaxTimeOfSuccess, methodStats.Value.MaxTimeOfSuccess);
                total.MaxTimeOfError = Math.Max(total.MaxTimeOfError, methodStats.Value.MaxTimeOfError);
                total.TotalSize += methodStats.Value.TotalSize;
                stringBuilder.AppendLine(PrepareReportLine(methodStats.Key, methodStats.Value));
            }

            stringBuilder.AppendLine(divider);
            stringBuilder.AppendLine(PrepareReportLine("TOTAL", total));
            stringBuilder.AppendLine(divider);

            _logger.Info(stringBuilder.ToString());
            _previousStats.Clear();
        }

        private void Swap()
        {
            (_currentStats, _previousStats) = (_previousStats, _currentStats);
        }

        [Pure]
        private string PrepareReportLine(in string key, MethodStats methodStats)
        {
            string reportLine = $"{key,-40}| " +
                                $"{methodStats.Successes.ToString(),9} | " +
                                $"{methodStats.AvgTimeOfSuccesses.ToString("0", CultureInfo.InvariantCulture),14} | " +
                                $"{methodStats.MaxTimeOfSuccess.ToString(CultureInfo.InvariantCulture),14} | " +
                                $"{methodStats.Errors.ToString(),9} | " +
                                $"{methodStats.AvgTimeOfErrors.ToString("0", CultureInfo.InvariantCulture),14} | " +
                                $"{methodStats.MaxTimeOfError.ToString(CultureInfo.InvariantCulture),14} | " +
                                $"{methodStats.AvgSize.ToString("0", CultureInfo.InvariantCulture),8} | " +
                                $"{methodStats.TotalSize.ToString("0", CultureInfo.InvariantCulture),10} | ";

            return reportLine;
        }
    }
}
