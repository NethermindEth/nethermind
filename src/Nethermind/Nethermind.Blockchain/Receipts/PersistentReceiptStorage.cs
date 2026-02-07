// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public class PersistentReceiptStorage : IReceiptStorage
    {
        private readonly IColumnsDb<ReceiptsColumns> _database;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly IDb _receiptsDb;
        private readonly IDb _defaultColumn;
        private readonly IDb _transactionDb;
        private static readonly Hash256 MigrationBlockNumberKey = Keccak.Compute(nameof(MigratedBlockNumber));
        private long _migratedBlockNumber;
        private readonly ReceiptArrayStorageDecoder _storageDecoder = ReceiptArrayStorageDecoder.Instance;
        private readonly IBlockTree _blockTree;
        private readonly IBlockStore _blockStore;
        private readonly IReceiptConfig _receiptConfig;
        private readonly bool _legacyHashKey;
        private IWriteBatch? _pendingBatch;
        private long _pendingBatchLastBlockNumber;
        private long _pendingBatchId = -1;
        private long _latestSeenBatchId = -1;
        private int _pendingBatchBlockCount;

        private const int CacheSize = 64;
        private const int MaxIndexedBlocksPerBatch = BlockProcessingConstants.MaxUncommittedBlocks;
        private readonly LruCache<ValueHash256, TxReceipt[]> _receiptsCache = new(CacheSize, CacheSize, "receipts");

        public event EventHandler<BlockReplacementEventArgs>? NewCanonicalReceipts;
        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;

        public PersistentReceiptStorage(
            IColumnsDb<ReceiptsColumns> receiptsDb,
            ISpecProvider specProvider,
            IReceiptsRecovery receiptsRecovery,
            IBlockTree blockTree,
            IBlockStore blockStore,
            IReceiptConfig receiptConfig,
            ReceiptArrayStorageDecoder? storageDecoder = null)
        {
            _database = receiptsDb ?? throw new ArgumentNullException(nameof(receiptsDb));
            _defaultColumn = _database.GetColumnDb(ReceiptsColumns.Default);
            long Get(Hash256 key, long defaultValue) => _defaultColumn.Get(key)?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;

            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _receiptsDb = _database.GetColumnDb(ReceiptsColumns.Blocks);
            _transactionDb = _database.GetColumnDb(ReceiptsColumns.Transactions);
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockStore = blockStore ?? throw new ArgumentNullException(nameof(blockStore));
            _storageDecoder = storageDecoder ?? ReceiptArrayStorageDecoder.Instance;
            _receiptConfig = receiptConfig ?? throw new ArgumentNullException(nameof(receiptConfig));

            _migratedBlockNumber = Get(MigrationBlockNumberKey, long.MaxValue);

            KeyValuePair<byte[], byte[]>? firstValue = _receiptsDb.GetAll().FirstOrDefault();
            _legacyHashKey = firstValue.HasValue && firstValue.Value.Key is not null && firstValue.Value.Key.Length == Hash256.Size;

            _blockTree.BlockAddedToMain += BlockTreeOnBlockAddedToMain;
        }

        private void BlockTreeOnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            Block newMain = e.Block;
            if (e.IsPartOfMainChainUpdate)
            {
                EnsureCanonicalBatched(newMain, e.MainChainUpdateId, e.IsLastInMainChainUpdate);
            }
            else
            {
                DiscardPendingBatch();
                EnsureCanonical(newMain);
            }

            NewCanonicalReceipts?.Invoke(this, e);

            long? txLookupLimit = _receiptConfig.TxLookupLimit;
            if (txLookupLimit is null or <= 0 || newMain.Number <= txLookupLimit.Value)
            {
                return;
            }

            // Don't block the main loop
            _ = Task.Run(() =>
            {
                Block? newOldTx = _blockTree.FindBlock(newMain.Number - txLookupLimit.Value);
                if (newOldTx is not null)
                {
                    RemoveBlockTx(newOldTx);
                }
            });
        }

        public Hash256 FindBlockHash(Hash256 txHash)
        {
            var blockHashData = _transactionDb.Get(txHash);
            if (blockHashData is null) return FindReceiptObsolete(txHash)?.BlockHash;

            if (blockHashData.Length == Hash256.Size) return new Hash256(blockHashData);

            long blockNum = new RlpStream(blockHashData).DecodeLong();
            return _blockTree.FindBlockHash(blockNum);
        }

        // Find receipt stored with old - obsolete format.
        private TxReceipt FindReceiptObsolete(Hash256 hash)
        {
            var receiptData = _defaultColumn.GetSpan(hash);
            try
            {
                return DeserializeReceiptObsolete(hash, receiptData);
            }
            finally
            {
                _defaultColumn.DangerousReleaseMemory(receiptData);
            }
        }

        private TxReceipt DeserializeReceiptObsolete(Hash256 hash, Span<byte> receiptData)
        {
            if (!receiptData.IsNullOrEmpty())
            {
                return _storageDecoder.DeserializeReceiptObsolete(hash, receiptData);
            }

            return null;
        }

        public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true)
        {
            if (block.ReceiptsRoot == Keccak.EmptyTreeHash)
            {
                return [];
            }

            Hash256 blockHash = block.Hash;
            if (_receiptsCache.TryGet(blockHash, out TxReceipt[]? receipts))
            {
                return receipts ?? [];
            }

            Span<byte> receiptsData = GetReceiptData(block.Number, blockHash);

            try
            {
                if (receiptsData.IsNullOrEmpty())
                {
                    return [];
                }
                else
                {
                    receipts = _storageDecoder.Decode(in receiptsData);

                    if (recover)
                    {
                        _receiptsRecovery.TryRecover(block, receipts, forceRecoverSender: recoverSender);
                        _receiptsCache.Set(blockHash, receipts);
                    }

                    return receipts;
                }
            }
            finally
            {
                _receiptsDb.DangerousReleaseMemory(receiptsData);
            }
        }

        [SkipLocalsInit]
        private unsafe Span<byte> GetReceiptData(long blockNumber, Hash256 blockHash)
        {
            Span<byte> blockNumPrefixed = stackalloc byte[40];
            if (_legacyHashKey)
            {
                Span<byte> receiptsData = _receiptsDb.GetSpan(blockHash);
                if (!receiptsData.IsNull())
                {
                    return receiptsData;
                }

                GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);

                receiptsData = _receiptsDb.GetSpan(blockNumPrefixed);

                return receiptsData;
            }
            else
            {
                GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);

                Span<byte> receiptsData = _receiptsDb.GetSpan(blockNumPrefixed);
                if (receiptsData.IsNull())
                {
                    receiptsData = _receiptsDb.GetSpan(blockHash);
                }

                return receiptsData;
            }
        }

        private static void GetBlockNumPrefixedKey(long blockNumber, Hash256 blockHash, Span<byte> output)
        {
            blockNumber.WriteBigEndian(output);
            blockHash!.Bytes.CopyTo(output[8..]);
        }

        public TxReceipt[] Get(Hash256 blockHash, bool recover = true)
        {
            Block? block = _blockTree.FindBlock(blockHash);
            if (block is null) return [];
            return Get(block, recover, false);
        }

        public bool CanGetReceiptsByHash(long blockNumber) => blockNumber >= MigratedBlockNumber;

        public bool TryGetReceiptsIterator(long blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            if (_receiptsCache.TryGet(blockHash, out var receipts))
            {
                iterator = new ReceiptsIterator(receipts);
                return true;
            }

            if (!CanGetReceiptsByHash(blockNumber))
            {
                iterator = new ReceiptsIterator();
                return false;
            }

            Span<byte> receiptsData = GetReceiptData(blockNumber, blockHash);

            Func<IReceiptsRecovery.IRecoveryContext?> recoveryContextFactory = () => null;

            if (ReceiptArrayStorageDecoder.IsCompactEncoding(receiptsData))
            {
                recoveryContextFactory = () =>
                {
                    ReceiptRecoveryBlock? block = _blockStore.GetReceiptRecoveryBlock(blockNumber, blockHash);

                    if (!block.HasValue)
                    {
                        throw new InvalidOperationException($"Unable to recover receipts for block {blockHash} because of missing block data.");
                    }

                    return _receiptsRecovery.CreateRecoveryContext(block.Value);
                };
            }

            IReceiptRefDecoder refDecoder = _storageDecoder.GetRefDecoder(receiptsData);

            iterator = new ReceiptsIterator(receiptsData, _receiptsDb, recoveryContextFactory, refDecoder);
            return true;
        }

        public void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical = true, WriteFlags writeFlags = WriteFlags.None, long? lastBlockNumber = null)
            => Insert(block, txReceipts, _specProvider.GetSpec(block.Header), ensureCanonical, writeFlags, lastBlockNumber);

        [SkipLocalsInit]
        public void Insert(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec, bool ensureCanonical = true, WriteFlags writeFlags = WriteFlags.None, long? lastBlockNumber = null)
        {
            txReceipts ??= [];
            int txReceiptsLength = txReceipts.Length;

            if (block.Transactions.Length != txReceiptsLength)
            {
                throw new InvalidDataException(
                    $"Block {block.ToString(Block.Format.FullHashAndNumber)} has different numbers " +
                    $"of transactions {block.Transactions.Length} and receipts {txReceipts.Length}.");
            }

            _receiptsRecovery.TryRecover(block, txReceipts, false);

            var blockNumber = block.Number;
            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;

            using (NettyRlpStream stream = _storageDecoder.EncodeToNewNettyStream(txReceipts, behaviors))
            {
                Span<byte> blockNumPrefixed = stackalloc byte[40];
                GetBlockNumPrefixedKey(blockNumber, block.Hash!, blockNumPrefixed);

                _receiptsDb.PutSpan(blockNumPrefixed, stream.AsSpan(), writeFlags);
            }

            if (blockNumber < MigratedBlockNumber)
            {
                MigratedBlockNumber = blockNumber;
            }

            _receiptsCache.Set(block.Hash, txReceipts);

            if (ensureCanonical)
            {
                EnsureCanonical(block, lastBlockNumber);
            }

            ReceiptsInserted?.Invoke(this, new(block.Header, txReceipts));
        }

        public long MigratedBlockNumber
        {
            get => _migratedBlockNumber;
            set
            {
                _migratedBlockNumber = value;
                _defaultColumn.PutSpan(MigrationBlockNumberKey.Bytes, value.ToBigEndianSpanWithoutLeadingZeros(out _));
            }
        }

        internal void ClearCache()
        {
            _receiptsCache.Clear();
        }

        [SkipLocalsInit]
        public bool HasBlock(long blockNumber, Hash256 blockHash)
        {
            if (_receiptsCache.Contains(blockHash)) return true;

            Span<byte> blockNumPrefixed = stackalloc byte[40];
            if (_legacyHashKey)
            {
                if (_receiptsDb.KeyExists(blockHash)) return true;

                GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);
                return _receiptsDb.KeyExists(blockNumPrefixed);
            }
            else
            {
                GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);
                return _receiptsDb.KeyExists(blockNumPrefixed) || _receiptsDb.KeyExists(blockHash);
            }
        }

        public void EnsureCanonical(Block block)
        {
            EnsureCanonical(block, null);
        }

        public void RemoveReceipts(Block block)
        {
            _receiptsCache.Delete(block.Hash);

            Span<byte> blockNumPrefixed = stackalloc byte[40];
            GetBlockNumPrefixedKey(block.Number, block.Hash, blockNumPrefixed);
            _receiptsDb.Remove(blockNumPrefixed);

            RemoveBlockTx(block);
        }

        private void RemoveBlockTx(Block block)
        {
            if (block.Transactions.Length == 0)
            {
                return;
            }

            using IWriteBatch writeBatch = _transactionDb.StartWriteBatch();
            foreach (Transaction tx in block.Transactions)
            {
                writeBatch[tx.Hash.Bytes] = null;
            }
        }

        private void EnsureCanonical(Block block, long? lastBlockNumber)
        {
            lastBlockNumber ??= _blockTree.FindBestSuggestedHeader()?.Number ?? 0;
            if (!ShouldIndexBlock(block, lastBlockNumber.Value, out Transaction[] transactions))
            {
                return;
            }

            using IWriteBatch writeBatch = _transactionDb.StartWriteBatch();
            WriteCanonicalTxIndex(block, transactions, writeBatch);
        }

        private void EnsureCanonicalBatched(Block block, long batchId, bool isLast)
        {
            bool shouldCommit = false;
            try
            {
                // Ignore out-of-order stale events from older updates.
                if (batchId < _latestSeenBatchId)
                {
                    return;
                }

                if (batchId > _latestSeenBatchId)
                {
                    _latestSeenBatchId = batchId;
                }

                if (_pendingBatchId >= 0 && _pendingBatchId != batchId)
                {
                    DiscardPendingBatch();
                }

                if (_pendingBatchId < 0)
                {
                    _pendingBatchId = batchId;
                    _pendingBatchLastBlockNumber = _blockTree.FindBestSuggestedHeader()?.Number ?? 0;
                    _pendingBatchBlockCount = 0;
                }

                if (ShouldIndexBlock(block, _pendingBatchLastBlockNumber, out Transaction[] transactions))
                {
                    _pendingBatch ??= _transactionDb.StartWriteBatch();
                    WriteCanonicalTxIndex(block, transactions, _pendingBatch);
                    if (++_pendingBatchBlockCount >= MaxIndexedBlocksPerBatch && !isLast)
                    {
                        FlushPendingBatchWrites();
                    }
                }

                shouldCommit = isLast && _pendingBatchId == batchId;
            }
            finally
            {
                if (shouldCommit)
                {
                    CommitPendingBatch();
                }
            }
        }

        private bool ShouldIndexBlock(Block block, long lastBlockNumber, out Transaction[] transactions)
        {
            transactions = block.Transactions;
            if (transactions.Length == 0) return false;

            long? limit = _receiptConfig.TxLookupLimit;
            return limit != -1 && (limit is null or 0 || block.Number > lastBlockNumber - limit.Value);
        }

        private void WriteCanonicalTxIndex(Block block, Transaction[] transactions, IWriteBatch writeBatch)
        {
            if (_receiptConfig.CompactTxIndex)
            {
                byte[] blockNumber = Rlp.Encode(block.Number).Bytes;
                foreach (Transaction tx in transactions)
                {
                    tx.Hash ??= tx.CalculateHash();
                    Hash256 hash = tx.Hash;
                    writeBatch[hash.Bytes] = blockNumber;
                }
            }
            else
            {
                ReadOnlySpan<byte> blockHash = block.Hash.Bytes;
                foreach (Transaction tx in transactions)
                {
                    tx.Hash ??= tx.CalculateHash();
                    Hash256 hash = tx.Hash;
                    writeBatch.PutSpan(hash.Bytes, blockHash);
                }
            }
        }

        private void CommitPendingBatch()
        {
            try { FlushPendingBatchWrites(); }
            finally { _pendingBatchId = -1; }
        }

        private void DiscardPendingBatch()
        {
            if (_pendingBatchId < 0) return;
            try { _pendingBatch?.Clear(); _pendingBatch?.Dispose(); }
            finally { _pendingBatch = null; _pendingBatchId = -1; _pendingBatchBlockCount = 0; }
        }

        private void FlushPendingBatchWrites()
        {
            try { _pendingBatch?.Dispose(); }
            finally { _pendingBatch = null; _pendingBatchBlockCount = 0; }
        }
    }
}
