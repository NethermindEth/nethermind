﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.BeaconNode.Services;

namespace Nethermind.BeaconNode.MockedStart
{
    /// <summary>
    /// Clock that runs at normal pace, but starting at the specified unix time.
    /// </summary>
    public class QuickStartClock : IClock
    {
        private readonly TimeSpan _adjustment;

        public QuickStartClock(ulong startTime)
        {
            _adjustment = TimeSpan.FromSeconds((long)startTime - DateTimeOffset.Now.ToUnixTimeSeconds());
        }

        public DateTimeOffset UtcNow() => DateTimeOffset.Now + _adjustment;
    }
}
