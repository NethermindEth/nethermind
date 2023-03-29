// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using DotNetty.Buffers;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
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
        private static readonly ReceiptStorageDecoder StorageDecoder = ReceiptStorageDecoder.Instance;
        private readonly IBlockFinder _blockFinder;

        private const int CacheSize = 64;
        private readonly LruCache<KeccakKey, TxReceipt[]> _receiptsCache = new(CacheSize, CacheSize, "receipts");

        public PersistentReceiptStorage(IColumnsDb<ReceiptsColumns> receiptsDb, ISpecProvider specProvider, IReceiptsRecovery receiptsRecovery, IBlockFinder blockFinder)
        {
            long Get(Keccak key, long defaultValue) => _database.Get(key)?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;

            _database = receiptsDb ?? throw new ArgumentNullException(nameof(receiptsDb));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _blocksDb = _database.GetColumnDb(ReceiptsColumns.Blocks);
            _transactionDb = _database.GetColumnDb(ReceiptsColumns.Transactions);
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));

            byte[] lowestBytes = _database.Get(Keccak.Zero);
            _lowestInsertedReceiptBlock = lowestBytes is null ? (long?)null : new RlpStream(lowestBytes).DecodeLong();
            _migratedBlockNumber = Get(MigrationBlockNumberKey, long.MaxValue);
        }

        public Keccak FindBlockHash(Keccak txHash)
        {
            var blockHashData = _transactionDb.Get(txHash);
            return blockHashData is null ? FindReceiptObsolete(txHash)?.BlockHash : new Keccak(blockHashData);
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
            if (!receiptData.IsNullOrEmpty())
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

            Keccak blockHash = block.Hash;
            if (_receiptsCache.TryGet(blockHash, out TxReceipt[]? receipts))
            {
                return receipts ?? Array.Empty<TxReceipt>();
            }

            Span<byte> receiptsData = _blocksDb.GetSpan(blockHash);
            try
            {
                if (receiptsData.IsNullOrEmpty())
                {
                    return Array.Empty<TxReceipt>();
                }
                else
                {
                    receipts = DecodeArray(receiptsData);

                    _receiptsRecovery.TryRecover(block, receipts);

                    _receiptsCache.Set(blockHash, receipts);
                    return receipts;
                }
            }
            finally
            {
                _blocksDb.DangerousReleaseMemory(receiptsData);
            }
        }

        public TxReceipt[] Get(Keccak blockHash)
        {
            Block? block = _blockFinder.FindBlock(blockHash);
            if (block == null) return Array.Empty<TxReceipt>();
            return Get(block);
        }

        private static TxReceipt[] DecodeArray(in Span<byte> receiptsData)
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

        public bool CanGetReceiptsByHash(long blockNumber) => blockNumber >= MigratedBlockNumber;

        public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator)
        {
            if (_receiptsCache.TryGet(blockHash, out var receipts))
            {
                iterator = new ReceiptsIterator(receipts);
                return true;
            }

            var result = CanGetReceiptsByHash(blockNumber);
            var receiptsData = _blocksDb.GetSpan(blockHash);
            var block = _blockFinder.FindBlock(blockHash);
            iterator = result ? new ReceiptsIterator(receiptsData, _blocksDb, block, _receiptsRecovery) : new ReceiptsIterator();
            return result;
        }

        public void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical = true)
        {
            txReceipts ??= Array.Empty<TxReceipt>();
            int txReceiptsLength = txReceipts.Length;

            if (block.Transactions.Length != txReceiptsLength)
            {
                throw new InvalidDataException(
                    $"Block {block.ToString(Block.Format.FullHashAndNumber)} has different numbers " +
                    $"of transactions {block.Transactions.Length} and receipts {txReceipts.Length}.");
            }

            _receiptsRecovery.TryRecover(block, txReceipts, forceRecoverSender: false, recoverSenderOnly: true);

            var blockNumber = block.Number;
            var spec = _specProvider.GetSpec(block.Header);
            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;
            using (NettyRlpStream stream = StorageDecoder.EncodeToNewNettyStream(txReceipts, behaviors))
            {
                _blocksDb.Set(block.Hash!, stream.AsSpan());
            }

            if (blockNumber < MigratedBlockNumber)
            {
                MigratedBlockNumber = blockNumber;
            }

            if (ensureCanonical)
            {
                EnsureCanonical(block);
            }
        }

        public long? LowestInsertedReceiptBlockNumber
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

        internal void ClearCache()
        {
            _receiptsCache.Clear();
        }

        public bool HasBlock(Keccak hash)
        {
            return _receiptsCache.Contains(hash) || _blocksDb.KeyExists(hash);
        }

        public void EnsureCanonical(Block block)
        {
            TxReceipt[] receipts = Get(block);
            using IBatch batch = _transactionDb.StartBatch();
            foreach (TxReceipt txReceipt in receipts)
            {
                batch[txReceipt.TxHash.Bytes] = block.Hash.Bytes;
            }
        }
    }
}
