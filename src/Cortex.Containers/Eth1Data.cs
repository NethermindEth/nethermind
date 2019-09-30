using Hash = System.Byte; // Byte32

namespace Cortex.Containers
{
    public class Eth1Data
    {
        public Hash BlockHash { get; }
        public ulong DepositCount { get; }
        public Hash DepositRoot { get; }
    }
}
