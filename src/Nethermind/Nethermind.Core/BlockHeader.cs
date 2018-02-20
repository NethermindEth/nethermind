/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;

namespace Nethermind.Core
{
    public class BlockHeader
    {
        public const ulong GenesisBlockNumber = 0;

        internal BlockHeader()
        {
        }
        
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

        public Keccak ParentHash { get; internal set; }
        public Keccak OmmersHash { get; internal set;}
        public Address Beneficiary { get; internal set;}

        public Keccak StateRoot { get; set; }
        public Keccak TransactionsRoot { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Bloom Bloom { get; set; }
        public BigInteger Difficulty { get; internal set;}
        public BigInteger Number { get; internal set;}
        public long GasUsed { get; set; }
        public long GasLimit { get; internal set;}
        public BigInteger Timestamp { get; internal set;}
        public byte[] ExtraData { get; internal set;}
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