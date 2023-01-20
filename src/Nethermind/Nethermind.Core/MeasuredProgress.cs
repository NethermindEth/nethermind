// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class MeasuredProgress
    {
        private readonly ITimestamper _timestamper;

        public MeasuredProgress(ITimestamper? timestamper = null)
        {
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

        public bool HasEnded => UtcEndTime.HasValue;

        public void MarkEnd()
        {
            if (!UtcEndTime.HasValue)
            {
                UtcEndTime = _timestamper.UtcNow;
            }
        }

        public void Reset(long startValue)
        {
            LastMeasurement = UtcEndTime = null;
            UtcStartTime = _timestamper.UtcNow;
            StartValue = CurrentValue = startValue;
        }

        private long StartValue { get; set; }

        private DateTime? UtcStartTime { get; set; }

        private DateTime? UtcEndTime { get; set; }

        private DateTime? LastMeasurement { get; set; }

        private long LastValue { get; set; }

        public long CurrentValue { get; private set; }

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
    }
}
