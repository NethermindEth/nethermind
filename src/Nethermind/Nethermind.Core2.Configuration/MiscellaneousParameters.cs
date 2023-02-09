// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
