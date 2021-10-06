//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data
{
    /// <summary>
    /// A data object representing a block as being sent from the execution layer to the consensus layer.
    /// </summary>
    public class BlockRequestResult
    {
        public BlockRequestResult() : this(true)
        {
        }
        
        public BlockRequestResult(bool setDefaults = false)
        {
            if (setDefaults)
            {
                Difficulty = UInt256.Zero;
                Nonce = 0;
                MixHash = Keccak.Zero;
                Uncles = Array.Empty<Keccak>();
            }
        }
        
        public BlockRequestResult(Block block, Keccak random)
        {
            BlockHash = block.Hash!;
            ParentHash = block.ParentHash!;
            Coinbase = block.Beneficiary;
            StateRoot = block.StateRoot!;
            BlockNumber = block.Number;
            GasLimit = block.GasLimit;
            GasUsed = block.GasUsed;
            ReceiptRoot = block.ReceiptsRoot!;
            LogsBloom = block.Bloom!;
            Random = random;
            SetTransactions(block.Transactions);
            Difficulty = block.Difficulty;
            Nonce = block.Nonce;
            ExtraData = block.ExtraData!;
            MixHash = block.MixHash!;
            Uncles = block.Uncles.Select(o => o.Hash!);
            Timestamp = block.Timestamp;
            BaseFeePerGas = block.BaseFeePerGas;
        }

        public bool TryGetBlock(out Block? block)
        {
            try
            {
                BlockHeader header = new(ParentHash, Keccak.OfAnEmptySequenceRlp, Coinbase, Difficulty, BlockNumber, GasLimit, Timestamp, ExtraData)
                {
                    Hash = BlockHash,
                    ReceiptsRoot = ReceiptRoot,
                    StateRoot = StateRoot,
                    MixHash = MixHash,
                    Bloom = LogsBloom,
                    GasUsed = GasUsed,
                    BaseFeePerGas = BaseFeePerGas
                };
                Transaction[] transactions = GetTransactions();
                header.TxRoot = new TxTrie(transactions).RootHash;
                block = new Block(header, transactions, Array.Empty<BlockHeader>());
                return true;
            }
            catch (Exception)
            {
                block = null;
                return false;
            }
        }
        
        public UInt256 Difficulty { get; set; }
        public bool ShouldSerializeDifficulty() => false;
        public byte[] ExtraData { get; set; } = null!;
        public long GasLimit { get; set; }
        public long GasUsed { get; set; }
       
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak BlockHash { get; set; } = null!;
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Bloom LogsBloom { get; set; } = Bloom.Empty;
        public Keccak Random { get; set; } = Keccak.Zero;
        public Address? Coinbase { get; set; }
        public Keccak MixHash { get; set; } = null!;
        public bool ShouldSerializeMixHash() => false;
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public ulong Nonce { get; set; }
        public bool ShouldSerializeNonce() => false;
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public long BlockNumber { get; set; }
        public Keccak ParentHash { get; set; } = null!;
        public Keccak ReceiptRoot { get; set; } = null!;
        public Keccak StateRoot { get; set; } = null!;
        public byte[][] Transactions { get; set; } = Array.Empty<byte[]>();
        public IEnumerable<Keccak>? Uncles { get; set; }
        public bool ShouldSerializeUncles() => false;
        public UInt256 Timestamp { get; set; }

        public UInt256 BaseFeePerGas { get; set; }
        
        public override string ToString() => BlockHash == null ? $"{BlockNumber} null" : $"{BlockNumber} ({BlockHash})";

        public void SetTransactions(params Transaction[] transactions)
        {
            Transactions = new byte[transactions.Length][];
            for (int i = 0; i < Transactions.Length; i++)
            {
                Transactions[i] = Rlp.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
            }
        }

        public Transaction[] GetTransactions()
        {
            Transaction[] transactions = new Transaction[Transactions.Length];
            for (int i = 0; i < Transactions.Length; i++)
            {
                transactions[i] = Rlp.Decode<Transaction>(Transactions[i], RlpBehaviors.SkipTypedWrapping);
            }
            
            return transactions;
        }
    }
}
