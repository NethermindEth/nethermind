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

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class BlockForRpc
    {
        public BlockForRpc(Block block, bool includeFullTransactionData)
        {
            Number = block.Number;
            Hash = block.Hash;
            ParentHash = block.ParentHash;
            Nonce = block.Nonce;
            MixHash = block.MixHash;
            Sha3Uncles = block.OmmersHash;
            LogsBloom = block.Bloom;
            TransactionsRoot = block.TransactionsRoot;
            StateRoot = block.StateRoot;
            ReceiptsRoot = block.ReceiptsRoot;
            Miner = block.Beneficiary;
            Difficulty = block.Difficulty;
            TotalDifficulty = block.TotalDifficulty ?? 0;
            ExtraData = block.ExtraData;
            Size = BigInteger.Zero;
            GasLimit = block.GasLimit;
            GasUsed = block.GasUsed;
            Timestamp = block.Timestamp;
            Transactions = includeFullTransactionData ? block.Transactions.Select((t, idx) => new TransactionForRpc(block.Hash, block.Number, idx, t)) : null;
            TransactionHashes = includeFullTransactionData ? null : block.Transactions.Select(t => t.Hash);
            Uncles = block.Ommers.Select(o => o.Hash);
        }
        
        public BigInteger Number { get; set; }
        public Keccak Hash { get; set; }
        public Keccak ParentHash { get; set; }
        public BigInteger Nonce { get; set; }
        public Keccak MixHash { get; set; }
        public Keccak Sha3Uncles { get; set; }
        public Bloom LogsBloom { get; set; }
        public Keccak TransactionsRoot { get; set; }
        public Keccak StateRoot { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Address Miner { get; set; }
        public BigInteger Difficulty { get; set; }
        public BigInteger TotalDifficulty { get; set; }
        public byte[] ExtraData { get; set; }
        public BigInteger Size { get; set; }
        public BigInteger GasLimit { get; set; }
        public BigInteger GasUsed { get; set; }
        public BigInteger Timestamp { get; set; }
        public IEnumerable<TransactionForRpc> Transactions { get; set; }
        public IEnumerable<Keccak> TransactionHashes { get; set; }
        public IEnumerable<Keccak> Uncles { get; set; }
    }
}