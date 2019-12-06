using Cortex.Containers;

namespace Cortex.BeaconNode.Configuration
{
    public class SignatureDomains
    {
        public DomainType BeaconAttester { get; set; }
        public DomainType BeaconProposer { get; set; }
        public DomainType Deposit { get; set; }
        public DomainType Randao { get; set; }
        public DomainType VoluntaryExit { get; set; }
    }
}
