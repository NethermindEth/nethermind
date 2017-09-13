using System.Numerics;

namespace Nevermind.Core
{
    public class BlockHeader
    {
        public Keccak ParentHash { get; set; }
        public Keccak OmmersHash { get; set; }
        public Address Beneficiary { get; set; }
        public Keccak StateRoot { get; set; }
        public Keccak TransactionsRoot { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public BloomFilter LogsBloom { get; set; }
        public BigInteger Difficulty { get; set; }
        public long Number { get; set; }
        public long GasLimit { get; set; }
        public long GasUsed { get; set; }
        public long Timestamp { get; set; }
        public byte[] ExtraData { get; set; }
        public Keccak MixHash { get; set; }
        public BigInteger Nonce { get; set; }
    }
}