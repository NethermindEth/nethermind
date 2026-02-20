// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Core
{
    public class ProgressLogger
    {
        public const int QueuePaddingLength = 8;
        public const int SkippedPaddingLength = 7;
        public const int SpeedPaddingLength = 7;
        public const int BlockPaddingLength = 10;
        public const int PrefixAlignment = -13;

        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;
        private string _prefix;
        private (long, long, long, long) _lastReportState = (0, 0, 0, 0);
        private Func<ProgressLogger, string>? _formatter;

        public ProgressLogger(string prefix, ILogManager logManager, ITimestamper? timestamper = null)
        {
            _logger = logManager.GetClassLogger(nameof(ProgressLogger));
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

        public void IncrementSkipped(int skipped = 1)
        {
            Interlocked.Add(ref _skipped, skipped);
        }

        public void SetMeasuringPoint(bool resetCompletion = true)
        {
            if (resetCompletion) UtcEndTime = null;

            if (UtcStartTime is not null)
            {
                LastMeasurement = _timestamper.UtcNow;
                LastValue = CurrentValue;
            }

            if (_skipped != -1) _skipped = 0;
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
            LastMeasurement = UtcEndTime = _timestamper.UtcNow;
            UtcStartTime = _timestamper.UtcNow;
            StartValue = CurrentValue = LastValue = startValue;
            if (CurrentQueued != -1) CurrentQueued = 0;
            if (_skipped != -1) _skipped = 0;
            TargetValue = total;
        }

        private long _skipped = -1;

        private long StartValue { get; set; }

        private DateTime? UtcStartTime { get; set; }

        private DateTime? UtcEndTime { get; set; }

        private DateTime? LastMeasurement { get; set; }

        private long LastValue { get; set; }
        public long TargetValue { get; set; }

        public long CurrentValue { get; private set; }
        public long CurrentQueued { get; set; } = -1;

        private TimeSpan Elapsed => (UtcEndTime ?? _timestamper.UtcNow) - (UtcStartTime ?? DateTime.MinValue);
        private TimeSpan ElapsedSinceLastMeasurement => _timestamper.UtcNow - (LastMeasurement ?? DateTime.MinValue);

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

        public decimal SkippedPerSecond
        {
            get
            {
                if (_skipped == -1) return -1;
                if (UtcEndTime is not null)
                {
                    return 0;
                }

                decimal timePassed = (decimal)ElapsedSinceLastMeasurement.TotalSeconds;
                if (timePassed == 0M)
                {
                    return 0M;
                }

                return _skipped / timePassed;
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

                decimal timePassed = (decimal)ElapsedSinceLastMeasurement.TotalSeconds;
                if (timePassed == 0M)
                {
                    return 0M;
                }

                return (CurrentValue - LastValue) / timePassed;
            }
        }

        public void SetFormat(Func<ProgressLogger, string> formatter)
        {
            _formatter = formatter;
        }

        public void LogProgress()
        {
            (long, long, long, long) reportState = (CurrentValue, TargetValue, CurrentQueued, _skipped);
            if (reportState != _lastReportState)
            {
                string reportString = _formatter is not null ? _formatter(this) : DefaultFormatter();
                _lastReportState = reportState;
                _logger.Info(reportString);
            }
            SetMeasuringPoint(resetCompletion: false);
        }

        private string DefaultFormatter()
        {
            return GenerateReport(_prefix, CurrentValue, TargetValue, CurrentQueued, CurrentPerSecond, SkippedPerSecond);
        }

        private static string GenerateReport(string prefix, long current, long total, long queue, decimal speed, decimal skippedPerSecond)
        {
            float percentage = Math.Clamp(current / (float)(Math.Max(total, 1)), 0, 1);
            string queuedStr = (queue >= 0 ? $" queue {queue,QueuePaddingLength:N0} | " : " ");
            string skippedStr = (skippedPerSecond >= 0 ? $"skipped {skippedPerSecond,SkippedPaddingLength:N0} Blk/s | " : "");
            string speedStr = $"current {speed,SpeedPaddingLength:N0} Blk/s";
            string receiptsReport =
                $"{prefix,PrefixAlignment}" +
                $"{current,BlockPaddingLength:N0} / {total,BlockPaddingLength:N0} ({percentage.ToString("P2", CultureInfo.InvariantCulture),8}) " +
                Progress.GetMeter(percentage, 1) +
                queuedStr +
                skippedStr +
                speedStr;
            return receiptsReport;
        }
    }
}
