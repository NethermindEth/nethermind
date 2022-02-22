using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithStorageSlot
    {
        public PathWithStorageSlot(Keccak keyHash, byte[] slotValue)
        {
            Path = keyHash;
            SlotValue = slotValue;
        }

        public Keccak Path { get; set; }
        public byte[] SlotValue { get; set; }
    }
}
