using Cortex.Containers;

namespace Cortex.BeaconNode.MockedStart
{
    public class QuickStartParameters
    {
        public Hash32 Eth1BlockHash { get; set; } = Hash32.Zero;
        public ulong Eth1Timestamp { get; set; }
        public ulong GenesisTime { get; set; }
        public ulong ValidatorCount { get; set; }
    }
}
