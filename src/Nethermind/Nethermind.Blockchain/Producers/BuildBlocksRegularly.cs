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
// 

using System;
using System.Threading;
using System.Timers;
using Nethermind.Core;
using Timer = System.Timers.Timer;

namespace Nethermind.Blockchain.Producers
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
