using System;
using Hash = System.Byte; // Byte32

namespace Cortex.Containers
{
    public class Eth1Data
    {
        public Eth1Data(ReadOnlySpan<byte> eth1BlockHash, ulong depositCount)
        {
            BlockHash = eth1BlockHash.ToArray();
            DepositCount = depositCount;
        }

        public Hash[] BlockHash { get; }
        public ulong DepositCount { get; }
        public Hash DepositRoot { get; }
    }
}
