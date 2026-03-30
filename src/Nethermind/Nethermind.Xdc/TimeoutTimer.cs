// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Timers;

namespace Nethermind.Xdc
{
    internal class TimeoutTimer : IDisposable, ITimeoutTimer
    {
        private readonly System.Timers.Timer timer;

        public TimeoutTimer()
        {
            timer = new System.Timers.Timer();
            timer.AutoReset = false;
            timer.Elapsed += (s, e) => TimeoutElapsed?.Invoke(s, e);
        }

        public event EventHandler<ElapsedEventArgs> TimeoutElapsed;

        public void Start(TimeSpan period)
        {
            timer.Interval = period.TotalMilliseconds;
            timer.Enabled = true;
            timer.Start();
        }

        public void Reset(TimeSpan period)
        {
            timer.Interval = period.TotalMilliseconds;
            timer.Enabled = true;
        }

        public void Dispose()
        {
            timer?.Dispose();
        }

        public void TriggerTimeout()
        {
            TimeoutElapsed?.Invoke(this, new ElapsedEventArgs(DateTime.UtcNow));
        }
    }
}
