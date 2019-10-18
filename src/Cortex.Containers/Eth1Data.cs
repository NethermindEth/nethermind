using System;

namespace Cortex.Containers
{
    public class Eth1Data
    {
        public Eth1Data(Hash32 eth1BlockHash, ulong depositCount)
            : this(eth1BlockHash, depositCount, new Hash32())
        {
        }

        public Eth1Data(Hash32 eth1BlockHash, ulong depositCount, Hash32 depositRoot)
        {
            BlockHash = eth1BlockHash;
            DepositCount = depositCount;
            DepositRoot = depositRoot;
        }

        public Hash32 BlockHash { get; }
        public ulong DepositCount { get; }
        public Hash32 DepositRoot { get; private set; }

        public void SetDepositRoot(Hash32 depositRoot)
        {
            if (depositRoot == null)
            {
                throw new ArgumentNullException(nameof(depositRoot));
            }
            DepositRoot = depositRoot;
        }
    }
}
