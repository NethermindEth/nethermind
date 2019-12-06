using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Configuration
{
    public class StateListLengths
    {
        public uint EpochsPerHistoricalVector { get; set; }
        public uint EpochsPerSlashingsVector { get; set; }
        public ulong HistoricalRootsLimit { get; set; }
        public ulong ValidatorRegistryLimit { get; set; }
    }
}
