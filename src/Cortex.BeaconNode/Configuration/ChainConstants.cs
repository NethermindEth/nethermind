using Cortex.Containers;

namespace Cortex.BeaconNode.Configuration
{
    public class ChainConstants
    {
        public int DepositContractTreeDepth { get; } = 1 << 5;

        public Epoch FarFutureEpoch { get; } = new Epoch((ulong)1 << 64 - 1);

        public int JustificationBitsLength { get; } = 4;

        public ulong MaximumDepositContracts { get; } = (ulong)1 << (1 << 5);

        public ulong SecondsPerDay { get; } = 24 * 60 * 60;
    }
}
