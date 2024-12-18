// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;

namespace Nethermind.Core
{
    public class MeasuredProgress
    {
        public const int QueuePaddingLength = 8;
        public const int SpeedPaddingLength = 7;
        public const int BlockPaddingLength = 10;

        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private string _prefix;
        private string _lastReport = "";
        private Func<MeasuredProgress, string>? _formatter;

        public MeasuredProgress(string prefix, ILogManager logManager, ITimestamper? timestamper = null)
        {
            _logger = logManager.GetClassLogger<MeasuredProgress>();
            _prefix = prefix;
            _timestamper = timestamper ?? Timestamper.Default;
        }

        public void Update(long value)
        {
            UtcEndTime = null;

            if (!UtcStartTime.HasValue)
            {
                UtcStartTime = _timestamper.UtcNow;
                StartValue = value;
            }

            CurrentValue = value;
        }

        public void SetMeasuringPoint()
        {
            UtcEndTime = null;

            if (UtcStartTime is not null)
            {
                LastMeasurement = _timestamper.UtcNow;
                LastValue = CurrentValue;
            }
        }

        public bool HasStarted => UtcStartTime.HasValue;

        public bool HasEnded => UtcEndTime.HasValue;

        public void MarkEnd()
        {
            if (CurrentQueued != -1) CurrentQueued = 0;
            if (!UtcEndTime.HasValue)
            {
                UtcEndTime = _timestamper.UtcNow;
            }
        }

        public void Reset(long startValue, long total)
        {
            LastMeasurement = UtcEndTime = null;
            UtcStartTime = _timestamper.UtcNow;
            StartValue = CurrentValue = startValue;
            if (CurrentQueued != -1) CurrentQueued = 0;
            TargetValue = total;
        }

        private long StartValue { get; set; }

        private DateTime? UtcStartTime { get; set; }

        private DateTime? UtcEndTime { get; set; }

        private DateTime? LastMeasurement { get; set; }

        private long LastValue { get; set; }
        public long TargetValue { get; set; }

        public long CurrentValue { get; private set; }
        public long CurrentQueued { get; set; } = -1;

        private TimeSpan Elapsed => (UtcEndTime ?? _timestamper.UtcNow) - (UtcStartTime ?? DateTime.MinValue);

        public decimal TotalPerSecond
        {
            get
            {
                decimal timePassed = (decimal)Elapsed.TotalSeconds;
                if (timePassed == 0M)
                {
                    return 0M;
                }

                return (CurrentValue - StartValue) / timePassed;
            }
        }

        public decimal CurrentPerSecond
        {
            get
            {
                if (UtcEndTime is not null)
                {
                    return 0;
                }

                decimal timePassed = (decimal)(_timestamper.UtcNow - (LastMeasurement ?? DateTime.MinValue)).TotalSeconds;
                if (timePassed == 0M)
                {
                    return 0M;
                }

                return (CurrentValue - LastValue) / timePassed;
            }
        }

        public void SetFormat(Func<MeasuredProgress, string> formatter)
        {
            _formatter = formatter;
        }

        public void LogProgress()
        {
            string reportString = _formatter is not null ? _formatter(this) : DefaultFormatter();
            if (!EqualsIgnoringSpeed(_lastReport, reportString))
            {
                _lastReport = reportString;
                _logger.Info(reportString);
            }
            SetMeasuringPoint();
        }

        private string DefaultFormatter()
        {
            return GenerateReport(_prefix, CurrentValue, TargetValue, CurrentQueued, CurrentPerSecond);
        }

        private static string GenerateReport(string prefix, long current, long total, long queue, decimal speed)
        {
            float percentage = Math.Clamp(current / (float)(total + 1), 0, 1);
            string receiptsReport = $"{prefix} {current,BlockPaddingLength:N0} / {total,BlockPaddingLength:N0} ({percentage,8:P2}) {Progress.GetMeter(percentage, 1)}{(queue >= 0 ? $" queue {queue,QueuePaddingLength:N0} " : "                ")}| current {speed,SpeedPaddingLength:N0} Blk/s";
            return receiptsReport;
        }

        private static bool EqualsIgnoringSpeed(string? lastReport, string report)
            => lastReport.AsSpan().StartsWith(report.AsSpan(0, report.LastIndexOf("| current")));
    }
}
