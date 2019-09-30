using System;

using Slot = System.UInt64;
using Hash = System.Byte; // Byte32
using BlsSignature = System.Byte; // Byte96

namespace Cortex.Containers
{
    public class Eth1Data
    {
        public Hash DepositRoot { get; }
        public ulong DepositCount { get; }
        public Hash BlockHash { get; }
    }
}
