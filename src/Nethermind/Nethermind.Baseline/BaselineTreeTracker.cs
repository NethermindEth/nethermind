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
using Nethermind.Blockchain.Find;
using Nethermind.Core;

namespace Nethermind.Baseline
{
    public class BaselineTreeTracker
    {
        private readonly Address _address;
        private readonly BaselineTree _baselineTree;
        private readonly ILogFinder _logFinder;
        private readonly IBlockFinder _blockFinder;
        private Timer _timer;

        /// <summary>
        /// This class should smoothly react to new blocks and logs
        /// For now it will be very non-optimized just to deliver the basic functionality
        /// </summary>
        /// <param name="address"></param>
        /// <param name="baselineTree"></param>
        /// <param name="logFinder"></param>
        /// <param name="blockFinder"></param>
        public BaselineTreeTracker(Address address, BaselineTree baselineTree, ILogFinder logFinder, IBlockFinder blockFinder)
        {
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _baselineTree = baselineTree ?? throw new ArgumentNullException(nameof(baselineTree));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));

            _timer = InitTimer();
        }
        
        private Timer InitTimer()
        {
            Timer timer = new Timer();
            timer.Interval = 1000;
            timer.Elapsed += TimerOnElapsed;
            timer.AutoReset = false;
            return timer;
        }
        
        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            _timer.Enabled = true;
        }
    }
}