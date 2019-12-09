using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Configuration
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
