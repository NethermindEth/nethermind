using System.Collections.Generic;
using System.Numerics;

namespace Ethereum.Test.Base
{
    public class AccountState
    {
        public BigInteger Balance { get; set; }
        public byte[] Code { get; set; }
        public BigInteger Nonce { get; set; }
        public Dictionary<BigInteger, byte[]> Storage { get; set; }
    }
}