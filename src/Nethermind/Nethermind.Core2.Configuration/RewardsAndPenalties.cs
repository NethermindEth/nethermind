// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core2.Configuration
{
    public class RewardsAndPenalties
    {
        public ulong BaseRewardFactor { get; set; }
        public ulong InactivityPenaltyQuotient { get; set; }
        public ulong MinimumSlashingPenaltyQuotient { get; set; }
        public ulong ProposerRewardQuotient { get; set; }
        public ulong WhistleblowerRewardQuotient { get; set; }
    }
}
