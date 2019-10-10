namespace Cortex.Containers
{
    public class Eth1Data
    {
        public Eth1Data(Hash32 eth1BlockHash, ulong depositCount)
        {
            BlockHash = eth1BlockHash;
            DepositCount = depositCount;
        }

        public Hash32 BlockHash { get; }
        public ulong DepositCount { get; }
        public Hash32 DepositRoot { get; }
    }
}
