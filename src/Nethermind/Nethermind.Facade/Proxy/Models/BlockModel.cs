//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Facade.Proxy.Models
{
    public class BlockModel
    {
        public UInt256 Difficulty { get; set; }
        public byte[] ExtraData { get; set; }
        public UInt256 GasLimit { get; set; }
        public UInt256 GasUsed { get; set; }
        public Keccak Hash { get; set; }
        public Address Miner { get; set; }
        public Keccak MixHash { get; set; }
        public UInt256 Nonce { get; set; }
        public UInt256 Number { get; set; }
        public Keccak ParentHash { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Keccak Sha3Uncles { get; set; }
        public UInt256 Size { get; set; }
        public Keccak StateRoot { get; set; }
        public UInt256 Timestamp { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public List<Keccak> Transactions { get; set; }
        public Keccak TransactionsRoot { get; set; }

        public Block ToBlock()
            => new Block(new BlockHeader(ParentHash, Sha3Uncles, Miner, Difficulty, (long) Number,
                (long) GasLimit, Timestamp, ExtraData))
            {
                StateRoot = StateRoot,
                GasUsed = (long) GasUsed,
                Hash = Hash,
                MixHash = MixHash,
                Nonce = (ulong) Nonce,
                ReceiptsRoot = ReceiptsRoot,
                TotalDifficulty = TotalDifficulty,
                TransactionsRoot = TransactionsRoot,
            };
    }
}