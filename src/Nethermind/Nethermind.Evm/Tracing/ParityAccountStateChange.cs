using System.Collections.Generic;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Tracing
{
    public class ParityAccountStateChange
    {
        public ParityStateChange<byte[]> Code { get; set; }
        public ParityStateChange<UInt256?> Balance { get; set; }
        public ParityStateChange<UInt256?> Nonce { get; set; }
        public Dictionary<UInt256, ParityStateChange<byte[]>> Storage { get; set; }
    }
}