using System;

using Slot = System.UInt64;
using Hash = System.Byte; // Byte32
using BlsSignature = System.Byte; // Byte96

namespace Cortex.Containers
{
    public class BeaconBlock
    {
        public Slot Slot  { get; }

        public Hash[] ParentRoot  { get; }

        public Hash[] StateRoot  { get; }

        public BeaconBlockBody Body  { get; }

        public BlsSignature[] Signature  { get; }

        public BeaconBlock(Slot slot, BlsSignature[] randaoReveal)        
        {
            Slot = slot;
            Body = new BeaconBlockBody(randaoReveal);
        }
   }
}
