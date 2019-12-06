using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Configuration
{
    public class InitialValues
    {
        public Epoch GenesisEpoch { get; set; }

        public byte BlsWithdrawalPrefix { get; set; }
    }
}
