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
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.MockedStart
{
    public class QuickStartParameters
    {
        public Hash32 Eth1BlockHash { get; set; } = Hash32.Zero;
        public ulong Eth1Timestamp { get; set; }
        public ulong GenesisTime { get; set; }
        public bool UseSystemClock { get; set; }
        public ulong ValidatorCount { get; set; }
        public long ClockOffset { get; set; }
        public ulong ValidatorStartIndex { get; set; }
        public ulong NumberOfValidators { get; set; }
    }
}
