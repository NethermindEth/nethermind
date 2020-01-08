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

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2;

namespace Nethermind.BeaconNode.Test.Helpers
{
    public class FastTestClock : IClock
    {
        private readonly Queue<DateTimeOffset> _timeValues;

        public FastTestClock(IEnumerable<DateTimeOffset> timeValues)
        {
            CompleteWaitHandle = new ManualResetEvent(false);
            _timeValues = new Queue<DateTimeOffset>(timeValues);
        }

        public ManualResetEvent CompleteWaitHandle { get; }

        public DateTimeOffset Now()
        {
            throw new NotImplementedException();
        }

        public DateTimeOffset UtcNow()
        {
            var next = _timeValues.Dequeue();
            if (_timeValues.Count == 0)
            {
                CompleteWaitHandle.Set();
            }
            return next;
        }
    }
}
