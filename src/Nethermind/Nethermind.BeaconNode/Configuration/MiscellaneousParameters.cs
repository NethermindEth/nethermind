namespace Nethermind.BeaconNode.Configuration
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
    }
}
