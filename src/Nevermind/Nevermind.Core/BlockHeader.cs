using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class BlockHeader
    {
        public const ulong GenesisBlockNumber = 0;

        public BlockHeader(Keccak parentHash, Keccak ommersHash, Address beneficiary, BigInteger difficulty, BigInteger number, long gasLimit, BigInteger timestamp, byte[] extraData)
        {
            ParentHash = parentHash;
            OmmersHash = ommersHash;
            Beneficiary = beneficiary;
            Difficulty = difficulty;
            Number = number;
            GasLimit = gasLimit;
            Timestamp = timestamp;
            ExtraData = extraData;
        }

        public Keccak ParentHash { get; }
        public Keccak OmmersHash { get; }
        public Address Beneficiary { get; }

        public Keccak StateRoot { get; set; }
        public Keccak TransactionsRoot { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Bloom Bloom { get; set; }
        public BigInteger Difficulty { get; }
        public BigInteger Number { get; }
        public long GasUsed { get; set; }
        public long GasLimit { get; }
        public BigInteger Timestamp { get; }
        public byte[] ExtraData { get; }
        public Keccak MixHash { get; set; }
        public ulong Nonce { get; set; }
        public Keccak Hash { get; set; }

        ////static BlockHeader()
        ////{
        ////    Genesis = new BlockHeader(null, new BlockHeader[] { }, new Transaction[] { });
        ////    Genesis.Number = GenesisBlockNumber;
        ////    Genesis.ParentHash = Keccak.Zero;
        ////    Genesis.OmmersHash = Keccak.Compute(Rlp.OfEmptySequence);
        ////    Genesis.Beneficiary = Address.Zero;
        ////    // state root
        ////    Genesis.TransactionsRoot = Keccak.Zero;
        ////    Genesis.ReceiptsRoot = Keccak.Zero;
        ////    Genesis.LogsBloom = new Bloom();
        ////    throw new NotImplementedException();
        ////    //Genesis.Difficulty = DifficultyCalculator.OfGenesisBlock;
        ////    Genesis.Number = 0;
        ////    Genesis.GasUsed = 0;
        ////    //Genesis.GasLimit = 3141592;
        ////    Genesis.GasLimit = 5000;
        ////    // timestamp
        ////    Genesis.ExtraData = Hex.ToBytes("0x11bbe8db4e347b4e8c937c1c8370e4b5ed33adb3db69cbdb7a38e1e50b1b82fa");
        ////    Genesis.MixHash = Keccak.Zero;
        ////    Genesis.Nonce = Rlp.Encode(new byte[] {42}).Bytes.ToUInt64();
        ////}

//        public static BlockHeader Genesis { get; }
        public void RecomputeHash()
        {
            Hash = Keccak.Compute(Rlp.Encode(this));
        }
    }
}