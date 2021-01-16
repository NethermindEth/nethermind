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

namespace Nethermind.Core
{
    /// <summary>
    /// Each time this timestamper is asked about the time it move the time forward by some constant
    /// </summary>
    public class IncrementalTimestamper : ITimestamper
    {
        private readonly TimeSpan _increment;
        private DateTime _utcNow;

        public IncrementalTimestamper()
            : this(DateTime.UtcNow, TimeSpan.FromSeconds(1)) { }

        public IncrementalTimestamper(DateTime initialValue, TimeSpan increment)
        {
            _increment = increment;
            _utcNow = initialValue;
        }

        public DateTime UtcNow
        {
            get
            {
                DateTime result = _utcNow;
                _utcNow = _utcNow.Add(_increment);
                return result;
            }
        }
    }
}
