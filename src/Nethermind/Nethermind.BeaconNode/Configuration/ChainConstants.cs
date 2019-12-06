using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Configuration
{
    public class ChainConstants
    {
        public ulong BaseRewardsPerEpoch { get; } = 4;

        public int DepositContractTreeDepth { get; } = 1 << 5;

        public Epoch FarFutureEpoch { get; } = new Epoch((ulong)1 << 64 - 1);

        public int JustificationBitsLength { get; } = 4;

        public ulong MaximumDepositContracts { get; } = (ulong)1 << (1 << 5);

        public ulong SecondsPerDay { get; } = 24 * 60 * 60;
    }
}
