// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Timers;

namespace Nethermind.Core.Timers
{
    public class TimerWrapper : ITimer
    {
        private readonly Timer _timer;

        public TimerWrapper(Timer timer)
        {
            _timer = timer;
            _timer.Elapsed += OnElapsed;
        }

        public bool AutoReset
        {
            get => _timer.AutoReset;
            set => _timer.AutoReset = value;
        }

        public bool Enabled
        {
            get => _timer.Enabled;
            set => _timer.Enabled = value;
        }

        public TimeSpan Interval
        {
            get => TimeSpan.FromMilliseconds(_timer.Interval);
            set => _timer.Interval = value.TotalMilliseconds;
        }

        public double IntervalMilliseconds
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public event EventHandler? Elapsed;

        public void Dispose()
        {
            _timer.Elapsed -= OnElapsed;
            _timer.Dispose();
        }

        private void OnElapsed(object? sender, ElapsedEventArgs e)
        {
            Elapsed?.Invoke(sender, e);
        }
    }
}
