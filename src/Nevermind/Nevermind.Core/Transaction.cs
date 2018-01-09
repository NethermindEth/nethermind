using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class Transaction
    {
        public ChainId ChainId { get; set; }
        public BigInteger Nonce { get; set; }
        public BigInteger GasPrice { get; set; }
        public BigInteger GasLimit { get; set; }
        public Address To { get; set; }
        public BigInteger Value { get; set; }
        public byte[] Data { get; set; }
        public byte[] Init { get; set; }
        public Signature Signature { get; set; }
        public bool IsSigned => Signature != null;
        public bool IsContractCreation => Init != null;
        public bool IsMessageCall => Data != null;
        public bool IsTransfer => !IsContractCreation && !IsMessageCall;
        public bool IsValid { get; set; }
        public Keccak Hash { get; set; }

        public void RecomputeHash()
        {
            Hash = Keccak.Compute(Rlp.Encode(this));
        }
    }
}