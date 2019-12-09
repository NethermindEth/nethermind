namespace Nethermind.BeaconNode.Configuration
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
