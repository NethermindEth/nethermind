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
    public class TimeParameters
    {
        public Epoch MaximumSeedLookahead { get; set; }

        public Slot MinimumAttestationInclusionDelay { get; set; }
        public Epoch MinimumEpochsToInactivityPenalty { get; set; }
        public uint MinimumGenesisDelay { get; set; }
        public Epoch MinimumSeedLookahead { get; set; }
        public Epoch MinimumValidatorWithdrawabilityDelay { get; set; }
        public Epoch PersistentCommitteePeriod { get; set; }
        public uint SecondsPerSlot { get; set; }
        public uint SlotsPerEpoch { get; set; }
        public uint SlotsPerEth1VotingPeriod { get; set; }
        public uint SlotsPerHistoricalRoot { get; set; }
    }
}
