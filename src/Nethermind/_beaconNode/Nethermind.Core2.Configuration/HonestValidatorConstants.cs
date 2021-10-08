using Nethermind.Core2.Types;

namespace Nethermind.Core2.Configuration
{
    public class HonestValidatorConstants
    {
        public Epoch EpochsPerRandomSubnetSubscription { get; set; }
        public ulong Eth1FollowDistance { get; set; }
        public ulong RandomSubnetsPerValidator { get; set; }
        public ulong SecondsPerEth1Block { get; set; }
        public ulong TargetAggregatorsPerCommittee { get; set; }
    }
}