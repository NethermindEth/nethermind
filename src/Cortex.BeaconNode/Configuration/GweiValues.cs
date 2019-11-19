using Cortex.Containers;

namespace Cortex.BeaconNode.Configuration
{
    public class GweiValues
    {
        public Gwei EffectiveBalanceIncrement { get; set; }
        public Gwei EjectionBalance { get; set; }
        public Gwei MaximumEffectiveBalance { get; set; }
    }
}
