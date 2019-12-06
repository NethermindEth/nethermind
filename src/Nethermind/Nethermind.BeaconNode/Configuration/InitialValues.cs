using Cortex.Containers;

namespace Cortex.BeaconNode.Configuration
{
    public class InitialValues
    {
        public Epoch GenesisEpoch { get; set; }

        public byte BlsWithdrawalPrefix { get; set; }
    }
}
