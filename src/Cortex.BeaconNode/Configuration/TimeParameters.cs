using Cortex.Containers;

namespace Cortex.BeaconNode.Configuration
{
    public class TimeParameters
    {
        public Epoch MinimumSeedLookahead { get; set; }
        public Slot SlotsPerEpoch { get; set; }
        public Slot SlotsPerHistoricalRoot { get; set; }
    }
}
