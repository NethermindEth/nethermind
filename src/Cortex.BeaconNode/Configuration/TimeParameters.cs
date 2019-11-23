using Cortex.Containers;

namespace Cortex.BeaconNode.Configuration
{
    public class TimeParameters
    {
        //public Epoch MaximumEpochsPerCrosslink { get; set; }
        public Epoch MaximumSeedLookahead { get; set; }

        public Slot MinimumAttestationInclusionDelay { get; set; }
        public Epoch MinimumEpochsToInactivityPenalty { get; set; }
        public Epoch MinimumSeedLookahead { get; set; }
        public Epoch MinimumValidatorWithdrawabilityDelay { get; set; }
        public Epoch PersistentCommitteePeriod { get; set; }
        public ulong SecondsPerSlot { get; set; }
        public Slot SlotsPerEpoch { get; set; }
        public Slot SlotsPerEth1VotingPeriod { get; set; }
        public Slot SlotsPerHistoricalRoot { get; set; }
    }
}
