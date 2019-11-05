using Cortex.Containers;

namespace Cortex.BeaconNode.Configuration
{
    public class StateListLengths
    {
        public Epoch EpochsPerHistoricalVector { get; set; }
        public Epoch EpochsPerSlashingsVector { get; set; }
        public ulong ValidatorRegistryLimit { get; set; }
    }
}
