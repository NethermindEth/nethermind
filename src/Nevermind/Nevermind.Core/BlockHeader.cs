using System;
using System.Numerics;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class BlockHeader
    {
        public const ulong GenesisBlockNumber = 0;

        public BlockHeader()
        {
        }

        public BlockHeader(BlockHeader parentBlockHeader, BlockHeader[] ommers, Transaction[] transactions)
        {
            if (parentBlockHeader == null)
            {
                return;
            }

            Timestamp = TimeStamp.Get();
            throw new NotImplementedException();
            //Difficulty = DifficultyCalculator.Calculate(this, parentBlockHeader);
        }

        public Keccak ParentHash { get; set; }
        public Keccak OmmersHash { get; set; }
        /// <summary>
        /// CoinBase
        /// </summary>
        public Address Beneficiary { get; set; }
        public Keccak StateRoot { get; set; }
        public Keccak TransactionsRoot { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Bloom LogsBloom { get; set; }
        public BigInteger Difficulty { get; set; }
        public BigInteger Number { get; set; }
        public BigInteger GasUsed { get; set; }
        public BigInteger GasLimit { get; set; }
        public BigInteger Timestamp { get; set; }
        public byte[] ExtraData { get; set; }
        public Keccak MixHash { get; set; }
        public ulong Nonce { get; set; }

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

        public static BlockHeader Genesis { get; }
    }
}