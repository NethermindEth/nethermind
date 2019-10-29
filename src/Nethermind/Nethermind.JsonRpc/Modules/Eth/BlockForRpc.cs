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

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class BlockForRpc
    {
        private BlockDecoder _blockDecoder = new BlockDecoder();
        
        public BlockForRpc(Block block, bool includeFullTransactionData)
        {
            bool isAuRaBlock = block.Header.AuRaSignature != null;
            Author = block.Author;
            Difficulty = block.Difficulty;
            ExtraData = block.ExtraData;
            GasLimit = block.GasLimit;
            GasUsed = block.GasUsed;
            Hash = block.Hash;
            LogsBloom = block.Bloom;
            Miner = block.Beneficiary;
            if (!isAuRaBlock)
            {
                MixHash = block.MixHash;
                Nonce = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(Nonce, block.Nonce);
            }
            else
            {
                Step = block.Header.AuRaStep;
                Signature = block.Header.AuRaSignature;                    
            }

            Number = block.Number;
            ParentHash = block.ParentHash;
            ReceiptsRoot = block.ReceiptsRoot;
            Sha3Uncles = block.OmmersHash;
            Size = _blockDecoder.GetLength(block, RlpBehaviors.None);
            StateRoot = block.StateRoot;

            
            Timestamp = block.Timestamp;
            TotalDifficulty = block.TotalDifficulty ?? 0;
            Transactions = includeFullTransactionData ? block.Transactions.Select((t, idx) => new TransactionForRpc(block.Hash, block.Number, idx, t)).ToArray() : (object[])block.Transactions.Select(t => t.Hash).ToArray();
            TransactionsRoot = block.TransactionsRoot;
            Uncles = block.Ommers.Select(o => o.Hash);
        }
        
        public Address Author { get; set; }
        public UInt256 Difficulty { get; set; }
        public byte[] ExtraData { get; set; }
        public long GasLimit { get; set; }
        public long GasUsed { get; set; }
        public Keccak Hash { get; set; }
        public Bloom LogsBloom { get; set; }
        public Address Miner { get; set; }
        public Keccak MixHash { get; set; }
        public byte[] Nonce { get; set; }
        public long Number { get; set; }
        public Keccak ParentHash { get; set; }
        public Keccak ReceiptsRoot { get; set; }
        public Keccak Sha3Uncles { get; set; }
        public byte[] Signature { get; set; }
        public long Size { get; set; }
        public Keccak StateRoot { get; set; }
        public long? Step { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public UInt256 Timestamp { get; set; }
        public IEnumerable<object> Transactions { get; set; }
        public Keccak TransactionsRoot { get; set; }
        public IEnumerable<Keccak> Uncles { get; set; }
    }
}