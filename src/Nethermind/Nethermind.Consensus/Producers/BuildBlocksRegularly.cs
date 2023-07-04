// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Timers;
using Timer = System.Timers.Timer;

namespace Nethermind.Consensus.Producers
{
    public class BuildBlocksRegularly : IBlockProductionTrigger, IDisposable
    {
        private readonly Timer _timer;

        public BuildBlocksRegularly(TimeSpan interval)
        {
            _timer = new Timer(interval.TotalMilliseconds);
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            TriggerBlockProduction?.Invoke(this, new BlockProductionEventArgs());
            _timer.Enabled = true;
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
