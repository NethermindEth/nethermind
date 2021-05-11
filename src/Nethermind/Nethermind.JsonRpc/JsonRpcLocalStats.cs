//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
        private DateTime _lastReport = DateTime.MinValue;
        private readonly ILogger _logger;

        public JsonRpcLocalStats(ITimestamper timestamper, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
        {
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _reportingInterval = TimeSpan.FromSeconds(jsonRpcConfig.ReportIntervalSeconds);
        }

        private class MethodStats
        {
            public int Successes { get; set; }
            public int Errors { get; set; }
            public decimal AvgTimeOfErrors { get; set; }
            public decimal AvgTimeOfSuccesses { get; set; }
            public long MaxTimeOfError { get; set; }
            public long MaxTimeOfSuccess { get; set; }
            public decimal TotalSize { get; set; }
            public decimal AvgSize => Calls == 0 ? 0 : TotalSize / Calls;
            public int Calls => Successes + Errors;
        }

        public void ReportCall(string method, long handlingTimeMicroseconds, bool success) =>
            ReportCall(new RpcReport(method, handlingTimeMicroseconds, success));
        
        public void ReportCall(in RpcReport report, long elapsedMicroseconds = 0, long? size = null)
        {
            if(string.IsNullOrWhiteSpace(report.Method))
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
            methodStats ??= _currentStats.GetOrAdd(report.Method, m => new MethodStats());

            long reportHandlingTimeMicroseconds = elapsedMicroseconds == 0 ? report.HandlingTimeMicroseconds : elapsedMicroseconds;

            decimal sizeDec = size ?? 0;

            lock (methodStats)
            {
                if (report.Success)
                {
                    methodStats.AvgTimeOfSuccesses = (methodStats.Successes * methodStats.AvgTimeOfSuccesses + reportHandlingTimeMicroseconds) / ++methodStats.Successes;
                    methodStats.MaxTimeOfSuccess = Math.Max(methodStats.MaxTimeOfSuccess, reportHandlingTimeMicroseconds);
                }
                else
                {
                    methodStats.AvgTimeOfErrors = (methodStats.Errors * methodStats.AvgTimeOfErrors + reportHandlingTimeMicroseconds) / ++methodStats.Errors;
                    methodStats.MaxTimeOfError = Math.Max(methodStats.MaxTimeOfError, reportHandlingTimeMicroseconds);
                }

                methodStats.TotalSize += sizeDec;
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
                                        " avg time | " +
                                        " max time | " +
                                        "   errors | " +
                                        " avg time | " +
                                        " max time |" +
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
            var temp = _currentStats;
            _currentStats = _previousStats;
            _previousStats = temp;
        }

        [Pure]
        private string PrepareReportLine(in string key, MethodStats methodStats)
        {
            string reportLine = $"{key.PadRight(40)}| " +
                                $"{methodStats.Successes.ToString().PadLeft(9)} | " +
                                $"{methodStats.AvgTimeOfSuccesses.ToString("0", CultureInfo.InvariantCulture).PadLeft(9)} | " +
                                $"{methodStats.MaxTimeOfSuccess.ToString(CultureInfo.InvariantCulture).PadLeft(9)} | " +
                                $"{methodStats.Errors.ToString().PadLeft(9)} | " +
                                $"{methodStats.AvgTimeOfErrors.ToString("0", CultureInfo.InvariantCulture).PadLeft(9)} | " +
                                $"{methodStats.MaxTimeOfError.ToString(CultureInfo.InvariantCulture).PadLeft(9)} | " +
                                $"{methodStats.AvgSize.ToString("0", CultureInfo.InvariantCulture).PadLeft(8)} | " +
                                $"{methodStats.TotalSize.ToString("0", CultureInfo.InvariantCulture).PadLeft(10)} | ";

            return reportLine;
        }
    }
}
