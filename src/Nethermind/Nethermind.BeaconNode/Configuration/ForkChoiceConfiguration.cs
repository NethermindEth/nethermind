using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Configuration
{
    public class ForkChoiceConfiguration
    {
        public Slot SafeSlotsToUpdateJustified { get; set; }
    }
}
