//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

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

        private void OnElapsed(object sender, ElapsedEventArgs e)
        {
            Elapsed?.Invoke(sender, e);
        }
    }
}
