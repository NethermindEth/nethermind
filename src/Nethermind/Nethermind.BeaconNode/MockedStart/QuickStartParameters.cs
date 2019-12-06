using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.MockedStart
{
    public class QuickStartParameters
    {
        public Hash32 Eth1BlockHash { get; set; } = Hash32.Zero;
        public ulong Eth1Timestamp { get; set; }
        public ulong GenesisTime { get; set; }
        public bool UseSystemClock { get; set; }
        public ulong ValidatorCount { get; set; }
    }
}
