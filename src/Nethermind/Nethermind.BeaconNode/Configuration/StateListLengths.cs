using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Configuration
{
    public class StateListLengths
    {
        public Epoch EpochsPerHistoricalVector { get; set; }
        public Epoch EpochsPerSlashingsVector { get; set; }
        public ulong HistoricalRootsLimit { get; set; }
        public ulong ValidatorRegistryLimit { get; set; }
    }
}
