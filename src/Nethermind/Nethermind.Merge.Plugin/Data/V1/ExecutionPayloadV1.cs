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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data.V1
{
    /// <summary>
    /// A data object representing a block as being sent from the execution layer to the consensus layer.
    ///
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#executionpayloadv1"/>
    /// </summary>
    public class ExecutionPayloadV1
    {
        public ExecutionPayloadV1()
        {
            BlockHash = Keccak.Zero;
            ParentHash = Keccak.Zero;
            FeeRecipient = Address.Zero;
            StateRoot = Keccak.Zero;
            ReceiptsRoot = Keccak.Zero;
            LogsBloom = Bloom.Empty;
            PrevRandao = Keccak.Zero;
            ExtraData = Array.Empty<byte>();
        }

        public ExecutionPayloadV1(Block block)
        {
            BlockHash = block.Hash!;
            ParentHash = block.ParentHash!;
            FeeRecipient = block.Beneficiary!;
            StateRoot = block.StateRoot!;
            BlockNumber = block.Number;
            GasLimit = block.GasLimit;
            GasUsed = block.GasUsed;
            ReceiptsRoot = block.ReceiptsRoot!;
            LogsBloom = block.Bloom!;
            PrevRandao = block.MixHash ?? Keccak.Zero;
            SetTransactions(block.Transactions);
            ExtraData = block.ExtraData!;
            Timestamp = block.Timestamp;
            BaseFeePerGas = block.BaseFeePerGas;
        }

        public bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
        {
            try
            {
                BlockHeader header = new(ParentHash, Keccak.OfAnEmptySequenceRlp, FeeRecipient, UInt256.Zero, BlockNumber, GasLimit, Timestamp, ExtraData)
                {
                    Hash = BlockHash,
                    ReceiptsRoot = ReceiptsRoot,
                    StateRoot = StateRoot,
                    Bloom = LogsBloom,
                    GasUsed = GasUsed,
                    BaseFeePerGas = BaseFeePerGas,
                    Nonce = 0,
                    MixHash = PrevRandao,
                    Author = FeeRecipient
                };
                Transaction[] transactions = GetTransactions();
                header.TxRoot = new TxTrie(transactions).RootHash;
                header.IsPostMerge = true;
                header.TotalDifficulty = totalDifficulty;
                block = new Block(header, transactions, Array.Empty<BlockHeader>());
                return true;
            }
            catch (Exception)
            {
                block = null;
                return false;
            }
        }

        public Keccak ParentHash { get; set; } = null!;
        public Address FeeRecipient { get; set; }
        public Keccak StateRoot { get; set; } = null!;
        public Keccak ReceiptsRoot { get; set; } = null!;

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Bloom LogsBloom { get; set; } = Bloom.Empty;
        public Keccak PrevRandao { get; set; } = Keccak.Zero;

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public long BlockNumber { get; set; }
        public long GasLimit { get; set; }
        public long GasUsed { get; set; }
        public ulong Timestamp { get; set; }
        public byte[] ExtraData { get; set; } = Array.Empty<byte>();
        public UInt256 BaseFeePerGas { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak? BlockHash { get; set; } = null!;

        /// <summary>
        /// Array of transaction objects, each object is a byte list (DATA) representing TransactionType || TransactionPayload or LegacyTransaction as defined in EIP-2718
        /// </summary>
        public byte[][] Transactions { get; set; } = Array.Empty<byte[]>();

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
