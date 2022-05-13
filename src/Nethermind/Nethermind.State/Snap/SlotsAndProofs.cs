using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.State.Snap
{
    public class SlotsAndProofs
    {
        public PathWithStorageSlot[][] PathsAndSlots { get; set; }
        public byte[][] Proofs { get; set; }
    }
}
