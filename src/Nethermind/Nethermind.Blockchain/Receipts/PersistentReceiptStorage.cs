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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Blockchain.Receipts
{
    public class PersistentReceiptStorage : IReceiptStorage
    {
        private readonly IColumnsDb<ReceiptsColumns> _database;
        private readonly ISpecProvider _specProvider;
        private long? _lowestInsertedReceiptBlock;
        private readonly IDb _blocksDb;
        private readonly IDb _transactionDb;

        public PersistentReceiptStorage(IColumnsDb<ReceiptsColumns> receiptsDb, ISpecProvider specProvider)
        {
            _database = receiptsDb ?? throw new ArgumentNullException(nameof(receiptsDb));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blocksDb = _database.GetColumnDb(ReceiptsColumns.Blocks);
            _transactionDb = _database.GetColumnDb(ReceiptsColumns.Transactions);

            byte[] lowestBytes = _database.Get(Keccak.Zero);
            _lowestInsertedReceiptBlock = lowestBytes == null ? (long?) null : new RlpStream(lowestBytes).DecodeLong();
        }

        public Keccak Find(Keccak hash)
        {
            var blockHashData = _transactionDb.Get(hash);
            return blockHashData == null ? FindObsolete(hash).BlockHash : new Keccak(blockHashData);
        }

        private TxReceipt FindObsolete(Keccak hash)
        {
            var receiptData = _database.Get(hash);
            if (receiptData != null)
            {
                try
                {
                    var receipt = Rlp.Decode<TxReceipt>(new Rlp(receiptData), RlpBehaviors.Storage);
                    receipt.TxHash = hash;
                    return receipt;
                }
                catch (RlpException)
                {
                    var receipt = Rlp.Decode<TxReceipt>(new Rlp(receiptData));
                    receipt.TxHash = hash;
                    return receipt;
                }
            }

            return null;
        }

        public TxReceipt[] Get(Block block)
        {
            if (block.ReceiptsRoot == Keccak.EmptyTreeHash)
            {
                return Array.Empty<TxReceipt>();
            }
            
            var receiptsData = _blocksDb.Get(block.Hash);
            if (receiptsData != null)
            {
                return Rlp.DecodeArray<TxReceipt>(new RlpStream(receiptsData));
            }
            else
            {
                TxReceipt[] result = new TxReceipt[block.Transactions.Length];
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    result[i] = FindObsolete(block.Transactions[i].Hash);
                }

                return result;
            }
        }

        public void Insert(Block block, params TxReceipt[] txReceipts)
        {
            txReceipts ??= Array.Empty<TxReceipt>();
            
            if (block.Transactions.Length != txReceipts.Length)
            {
                throw new ArgumentException($"Block {block.ToString(Block.Format.FullHashAndNumber)} has different number of transactions than receipts.");
            }
            
            var blockNumber = block.Number;
            var spec = _specProvider.GetSpec(blockNumber);
            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
            _blocksDb.Set(block.Hash, Rlp.Encode(txReceipts, behaviors).Bytes);
            
            for (int i = 0; i < txReceipts.Length; i++)
            {
                var txHash = block.Transactions[i].Hash;
                _transactionDb.Set(txHash, block.Hash.Bytes);
            }

            LowestInsertedReceiptBlock = Math.Min(LowestInsertedReceiptBlock ?? long.MaxValue, blockNumber);
        }

        public long? LowestInsertedReceiptBlock
        {
            get => _lowestInsertedReceiptBlock;
            private set
            {
                _lowestInsertedReceiptBlock = value;
                if (value.HasValue)
                {
                    _database.Set(Keccak.Zero, Rlp.Encode(value.Value).Bytes);
                }
            }
        }
    }
}