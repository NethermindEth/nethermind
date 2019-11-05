namespace Cortex.BeaconNode.Configuration
{
    public class RewardsAndPenalties
    {
        public ulong MinimumSlashingPenaltyQuotient { get; set; }
        public ulong ProposerRewardQuotient { get; set; }
        public ulong WhistleblowerRewardQuotient { get; set; }
    }
}
