using System;
using System.Numerics;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class BlockHeader
    {
        public BlockHeader(BlockHeader parentBlockHeader, BlockHeader[] ommers, Transaction[] transactions)
        {
            Number = parentBlockHeader.Number + 1;
            Timestamp = TimeStamp.Get();
            Difficulty = DifficultyCalculator.Calculate(this, parentBlockHeader);
            ParentHash = parentBlockHeader.MixHash;
        }

        public Keccak ParentHash { get; set; }
        public Keccak OmmersHash { get; set; }
        public Address Beneficiary { get; set; }
        public Keccak StateRoot { get; set; }
        public Keccak TransactionsRoot { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Bloom LogsBloom { get; set; }
        public BigInteger Difficulty { get; set; }
        public long Number { get; set; }
        public long GasUsed { get; set; }
        public long GasLimit { get; set; }
        public BigInteger Timestamp { get; set; }
        public byte[] ExtraData { get; set; }
        public Keccak MixHash { get; set; }
        public Keccak Nonce { get; set; }

        static BlockHeader()
        {
            Genesis = new BlockHeader(null, new BlockHeader[]{}, new Transaction[] {});
            Genesis.ParentHash = Keccak.Zero;
            Genesis.OmmersHash = Keccak.Compute(RecursiveLengthPrefix.OfEmptySequence);
            Genesis.Beneficiary = Address.Zero;
            // state root
            Genesis.TransactionsRoot = Keccak.Zero;
            Genesis.ReceiptsRoot = Keccak.Zero;
            Genesis.LogsBloom = new Bloom();
            Genesis.Difficulty = Core.DifficultyCalculator.OfGenesisBlock;
            Genesis.Number = 0;
            Genesis.GasUsed = 0;
            Genesis.GasLimit = 3141592;
            // timestamp
            Genesis.ExtraData = new byte[0];
            Genesis.MixHash = Keccak.Zero;
            // Genesis.Nonce = RecursiveLengthPrefix.Serialize(new byte[] {42});
        }

        public static BlockHeader Genesis { get; }
    }
}