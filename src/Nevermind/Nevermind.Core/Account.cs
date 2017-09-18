using System.Numerics;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class Account
    {
        public BigInteger Nonce { get; set; }
        public BigInteger Balance { get; set; }
        public Keccak StorageRoot { get; set; }
        public Keccak CodeHash { get; set; }

        // can be an extension
        public bool IsSimple => CodeHash == Keccak.OfEmptyString;
    }
}