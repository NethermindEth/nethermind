using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Configuration
{
    public class GweiValues
    {
        public Gwei EffectiveBalanceIncrement { get; set; }
        public Gwei EjectionBalance { get; set; }
        public Gwei MaximumEffectiveBalance { get; set; }
    }
}
