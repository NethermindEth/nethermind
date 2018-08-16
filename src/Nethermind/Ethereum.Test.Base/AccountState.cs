using System.Collections.Generic;
using System.Numerics;
using Nethermind.Dirichlet.Numerics;

namespace Ethereum.Test.Base
{
    public class AccountState
    {
        public UInt256 Balance { get; set; }
        public byte[] Code { get; set; }
        public UInt256 Nonce { get; set; }
        public Dictionary<BigInteger, byte[]> Storage { get; set; }
    }
}