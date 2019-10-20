using Cortex.Containers;

namespace Cortex.BeaconNode.Configuration
{
    public class MiscellaneousParameters
    {
        public ulong MaximumValidatorsPerCommittee { get; set; }

        public int MinimumGenesisActiveValidatorCount { get; set; }

        public ulong MinimumGenesisTime { get; set; }

        public Shard ShardCount { get; set; }

        public int ShuffleRoundCount { get; set; }

        public ulong TargetCommitteeSize { get; set; }
    }
}
