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

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Configuration
{
    public class QuickStartParameters
    {
        public Bytes32 Eth1BlockHash { get; set; } = Bytes32.Zero;
        /// <summary>
        /// Eth1Timestamp must be valid for the given Quickstart Genesis time (between MinimumGenesisDelay and 2 * MinimumGenesisDelay before genesis)
        /// </summary>
        public ulong Eth1Timestamp { get; set; }
        public ulong GenesisTime { get; set; }
        public bool UseSystemClock { get; set; }
        public ulong ValidatorCount { get; set; }
        public long ClockOffset { get; set; }
        public ulong ValidatorStartIndex { get; set; }
        public ulong NumberOfValidators { get; set; }
    }
}
