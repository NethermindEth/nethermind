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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
#pragma warning disable 618

namespace Nethermind.Blockchain.Receipts
{
    public class PersistentReceiptStorage : IReceiptStorage
    {
        private readonly IColumnsDb<ReceiptsColumns> _database;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private long? _lowestInsertedReceiptBlock;
        private readonly IDbWithSpan _blocksDb;
        private readonly IDb _transactionDb;
        private static readonly Keccak MigrationBlockNumberKey = Keccak.Compute(nameof(MigratedBlockNumber));
        private long _migratedBlockNumber;
        private static readonly ReceiptStorageDecoder StorageDecoder = new ReceiptStorageDecoder();

        public PersistentReceiptStorage(IColumnsDb<ReceiptsColumns> receiptsDb, ISpecProvider specProvider, IReceiptsRecovery receiptsRecovery)
        {
            long Get(Keccak key, long defaultValue) => _database.Get(key)?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;
            
            _database = receiptsDb ?? throw new ArgumentNullException(nameof(receiptsDb));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _blocksDb = _database.GetColumnDb(ReceiptsColumns.Blocks);
            _transactionDb = _database.GetColumnDb(ReceiptsColumns.Transactions);

            byte[] lowestBytes = _database.Get(Keccak.Zero);
            _lowestInsertedReceiptBlock = lowestBytes == null ? (long?) null : new RlpStream(lowestBytes).DecodeLong();
            _migratedBlockNumber = Get(MigrationBlockNumberKey, long.MaxValue);
        }

        public Keccak FindBlockHash(Keccak txHash)
        {
            var blockHashData = _transactionDb.Get(txHash);
            return blockHashData == null ? FindReceiptObsolete(txHash)?.BlockHash : new Keccak(blockHashData);
        }

        // Find receipt stored with old - obsolete format.
        private TxReceipt FindReceiptObsolete(Keccak hash)
        {
            var receiptData = _database.GetSpan(hash);
            try
            {
                return DeserializeReceiptObsolete(hash, receiptData);
            }
            finally
            {
                _database.DangerousReleaseMemory(receiptData);
            }
        }

        private static TxReceipt DeserializeReceiptObsolete(Keccak hash, Span<byte> receiptData)
        {
            if (receiptData != null)
            {
                var context = new Rlp.ValueDecoderContext(receiptData);
                try
                {
                    var receipt = StorageDecoder.Decode(ref context, RlpBehaviors.Storage);
                    receipt.TxHash = hash;
                    return receipt;
                }
                catch (RlpException)
                {
                    context.Position = 0;
                    var receipt = StorageDecoder.Decode(ref context);
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
            
            var receiptsData = _blocksDb.GetSpan(block.Hash);
            try
            {
                if (receiptsData != null)
                {
                    return DecodeArray(receiptsData);
                }
                else
                {
                    // didn't bring performance uplift that was expected
                    // var data = _database.MultiGet(block.Transactions.Select(t => t.Hash));
                    // return data.Select(kvp => DeserializeObsolete(new Keccak(kvp.Key), kvp.Value)).ToArray();

                    TxReceipt[] result = new TxReceipt[block.Transactions.Length];
                    for (int i = 0; i < block.Transactions.Length; i++)
                    {
                        result[i] = FindReceiptObsolete(block.Transactions[i].Hash);
                    }

                    return result;
                }
            }
            finally
            {
                _blocksDb.DangerousReleaseMemory(receiptsData);
            }
        }

        private static TxReceipt[] DecodeArray(Span<byte> receiptsData)
        {
            var decoderContext = new Rlp.ValueDecoderContext(receiptsData);
            try
            {
                return StorageDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
            }
            catch (RlpException)
            {
                decoderContext.Position = 0;
                return StorageDecoder.DecodeArray(ref decoderContext);
            }
        }

        public TxReceipt[] Get(Keccak blockHash)
        {
            var receiptsData = _blocksDb.GetSpan(blockHash);
            try
            {
                return receiptsData != null ? DecodeArray(receiptsData) : Array.Empty<TxReceipt>();
            }
            finally
            {
                _blocksDb.DangerousReleaseMemory(receiptsData);
            }
        }

        public bool CanGetReceiptsByHash(long blockNumber) => blockNumber >= MigratedBlockNumber;

        public void Insert(Block block, params TxReceipt[] txReceipts)
        {
            txReceipts ??= Array.Empty<TxReceipt>();
            
            if (block.Transactions.Length != txReceipts.Length)
            {
                throw new ArgumentException($"Block {block.ToString(Block.Format.FullHashAndNumber)} has different number of transactions than receipts.");
            }

            _receiptsRecovery.TryRecover(block, txReceipts);
            
            var blockNumber = block.Number;
            var spec = _specProvider.GetSpec(blockNumber);
            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;
            _blocksDb.Set(block.Hash, StorageDecoder.Encode(txReceipts, behaviors).Bytes);
            
            for (int i = 0; i < txReceipts.Length; i++)
            {
                var txHash = block.Transactions[i].Hash;
                _transactionDb.Set(txHash, block.Hash.Bytes);
            }

            if (blockNumber < (LowestInsertedReceiptBlock ?? long.MaxValue))
            {
                LowestInsertedReceiptBlock = blockNumber;
            }

            if (blockNumber < MigratedBlockNumber)
            {
                MigratedBlockNumber = blockNumber;
            }
        }

        public long? LowestInsertedReceiptBlock
        {
            get => _lowestInsertedReceiptBlock;
            set
            {
                _lowestInsertedReceiptBlock = value;
                if (value.HasValue)
                {
                    _database.Set(Keccak.Zero, Rlp.Encode(value.Value).Bytes);
                }
            }
        }
        
        public long MigratedBlockNumber
        {
            get => _migratedBlockNumber;
            set
            {
                _migratedBlockNumber = value;
                _database.Set(MigrationBlockNumberKey, MigratedBlockNumber.ToBigEndianByteArrayWithoutLeadingZeros());
            }
        }
    }
}