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

namespace Nethermind.Core2.Configuration
{
    public class MiscellaneousParameters
    {
        public ulong ChurnLimitQuotient { get; set; }

        public ulong MaximumCommitteesPerSlot { get; set; }

        public ulong MaximumValidatorsPerCommittee { get; set; }

        public int MinimumGenesisActiveValidatorCount { get; set; }

        public ulong MinimumGenesisTime { get; set; }

        //public Shard ShardCount { get; set; }

        public ulong MinimumPerEpochChurnLimit { get; set; }

        public int ShuffleRoundCount { get; set; }

        public ulong TargetCommitteeSize { get; set; }
        
        public ulong HysteresisQuotient { get; set; }
        public ulong HysteresisDownwardMultiplier { get; set; }
        public ulong HysteresisUpwardMultiplier { get; set; }
    }
}
