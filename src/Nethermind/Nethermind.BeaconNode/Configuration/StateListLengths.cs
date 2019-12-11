using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Configuration
{
    public class StateListLengths
    {
        public ulong EpochsPerHistoricalVector { get; set; }
        public ulong EpochsPerSlashingsVector { get; set; }
        public ulong HistoricalRootsLimit { get; set; }
        public ulong ValidatorRegistryLimit { get; set; }
    }
}
