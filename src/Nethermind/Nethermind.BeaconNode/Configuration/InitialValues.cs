using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Configuration
{
    public class InitialValues
    {
        public Epoch GenesisEpoch { get; set; }

        public byte BlsWithdrawalPrefix { get; set; }
    }
}
