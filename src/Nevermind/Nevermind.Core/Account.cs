using System.Numerics;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class Account
    {
        public Account()
        {
            Balance = BigInteger.Zero;
            Nonce = BigInteger.Zero;
            CodeHash = Keccak.OfAnEmptyString;
            StorageRoot = Keccak.OfAnEmptyString;
        }

        public BigInteger Nonce { get; set; }
        public BigInteger Balance { get; set; }
        public Keccak StorageRoot { get; set; }
        public Keccak CodeHash { get; set; }

        // can be an extension
        public bool IsSimple => CodeHash == Keccak.OfAnEmptyString;
    }
}