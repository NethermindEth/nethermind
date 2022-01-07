using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.SnapSync
{
    public class SlotWithKeyHash
    {
        public SlotWithKeyHash(Keccak keyHash, byte[] slotValue)
        {
            KeyHash = keyHash;
            SlotValue = slotValue;
        }

        public Keccak KeyHash { get; set; }
        public byte[] SlotValue { get; set; }
    }
}
