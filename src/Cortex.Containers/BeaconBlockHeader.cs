using System;
using BlsSignature = System.Byte; // Byte96

using Hash = System.Byte; // Byte32

using Slot = System.UInt64;

namespace Cortex.Containers
{
    public class BeaconBlockHeader
    {
        public BeaconBlockHeader(ReadOnlySpan<byte> bodyRoot)
        {
            BodyRoot = bodyRoot.ToArray();
        }

        public Hash[] BodyRoot { get; }
        public Hash[] ParentRoot { get; }
        public BlsSignature[] Signature { get; }
        public Slot Slot { get; }
        public Hash[] StateRoot { get; }
    }
}
