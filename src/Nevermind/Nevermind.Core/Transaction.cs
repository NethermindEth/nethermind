using System.Numerics;
using Nevermind.Core.Signing;

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
    }
}