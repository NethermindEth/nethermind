// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
