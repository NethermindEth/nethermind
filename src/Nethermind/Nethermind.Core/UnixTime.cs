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
    /// We have used before a construction that was ensuring that the exactly same
    /// timestamp is used when calculating both seconds and milliseconds.
    ///
    /// This struct achieves the same with a slightly neater code.
    /// </summary>
    public readonly struct UnixTime
    {
        private readonly DateTimeOffset _offset;

        public static UnixTime FromSeconds(double seconds)
        {
            return new(DateTime.UnixEpoch.Add(TimeSpan.FromSeconds(seconds)));
        }
        
        public UnixTime(DateTime dateTime) : this(new DateTimeOffset(dateTime))
        {
        }
        
        public UnixTime(DateTimeOffset dateTime)
        {
            _offset = dateTime;
        }

        public ulong Seconds => (ulong) SecondsLong;
        
        public ulong Milliseconds => (ulong) MillisecondsLong;
        
        public long SecondsLong => _offset.ToUnixTimeSeconds();
        
        public long MillisecondsLong => _offset.ToUnixTimeMilliseconds();
    }
}
