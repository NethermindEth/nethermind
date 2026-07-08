// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public class PersistentReceiptStorage : IReceiptStorage, IReceiptMigrationStore
    {
        private readonly IColumnsDb<ReceiptsColumns> _database;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly IDb _receiptsDb;
        private readonly IDb _defaultColumn;
        private readonly IDb _transactionDb;
        private static readonly Hash256 MigrationBlockNumberKey = Keccak.Compute(nameof(MigratedBlockNumber));
        private ulong _migratedBlockNumber;
        private readonly ReceiptArrayStorageDecoder _storageDecoder;
        private readonly IBlockTree _blockTree;
        private readonly IBlockStore _blockStore;
        private readonly IReceiptConfig _receiptConfig;
        private readonly bool _legacyHashKey;

        private const int CacheSize = 64;
        private readonly LruCache<ValueHash256, TxReceipt[]> _receiptsCache = new(CacheSize, CacheSize, "receipts");

        private readonly IDeferredBlockDataWriter? _deferredWriter;

        // Read-through overlay of receipts whose write is still queued. Payload = recovered receipts (for
        // reads) + pre-encoded RLP (the deferred write). Null when deferral is off.
        private readonly DeferredWriteOverlay<(TxReceipt[] Receipts, byte[] Rlp)>? _pendingReceipts;
        // Canonical tx-index served to FindBlockHash until the write lands: set on BlockAddedToMain, cleared by the write.
        private readonly ConcurrentDictionary<ValueHash256, PendingTxIndexValue> _pendingTxIndex = new();
        // Cancellation ledger for the queued canonical write: RemoveReceipts clears a block's entry so the write skips.
        private readonly ConcurrentDictionary<ValueHash256, PendingCanonicalEntry> _pendingCanonical = new();

        private sealed class PendingCanonicalEntry(Block block, ulong lastBlockNumber, PendingTxIndexValue txIndexValue)
        {
            public Block Block { get; } = block;
            public ulong LastBlockNumber { get; } = lastBlockNumber;
            public PendingTxIndexValue TxIndexValue { get; } = txIndexValue;
        }

        // Serialises the queued receipts write, the canonical-index write, and a synchronous removal. Shared with _pendingReceipts.
        private readonly Lock _writeLock = new();

        /// <summary>
        /// Mirrors the deferred tx-index write: block number under <see cref="IReceiptConfig.CompactTxIndex"/>
        /// (resolved canonically on read, so a reorged-out block self-heals like the persisted form), else block hash.
        /// </summary>
        private readonly record struct PendingTxIndexValue(ulong BlockNumber, Hash256? BlockHash);

        public event EventHandler<BlockReplacementEventArgs>? NewCanonicalReceipts;
        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;

        public PersistentReceiptStorage(
            IColumnsDb<ReceiptsColumns> receiptsDb,
            ISpecProvider specProvider,
            IReceiptsRecovery receiptsRecovery,
            IBlockTree blockTree,
            IBlockStore blockStore,
            IReceiptConfig receiptConfig,
            ReceiptArrayStorageDecoder? storageDecoder = null,
            IDeferredBlockDataWriter? deferredWriter = null,
            IStatePersistenceBarrier? persistenceBarrier = null)
        {
            _deferredWriter = deferredWriter is { Enabled: true } ? deferredWriter : null;
            _database = receiptsDb ?? throw new ArgumentNullException(nameof(receiptsDb));
            _defaultColumn = _database.GetColumnDb(ReceiptsColumns.Default);
            ulong Get(Hash256 key, ulong defaultValue) => _defaultColumn.Get(key)?.ToULongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;

            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _receiptsDb = _database.GetColumnDb(ReceiptsColumns.Blocks);
            _transactionDb = _database.GetColumnDb(ReceiptsColumns.Transactions);
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockStore = blockStore ?? throw new ArgumentNullException(nameof(blockStore));
            _storageDecoder = storageDecoder ?? ReceiptArrayStorageDecoder.Instance;
            _receiptConfig = receiptConfig ?? throw new ArgumentNullException(nameof(receiptConfig));

            _migratedBlockNumber = Get(MigrationBlockNumberKey, ulong.MaxValue);

            KeyValuePair<byte[], byte[]>? firstValue = _receiptsDb.GetAll().FirstOrDefault();
            _legacyHashKey = firstValue.HasValue && firstValue.Value.Key is not null && firstValue.Value.Key.Length == Hash256.Size;

            _blockTree.BlockAddedToMain += BlockTreeOnBlockAddedToMain;

            if (_deferredWriter is not null)
            {
                _pendingReceipts = new DeferredWriteOverlay<(TxReceipt[] Receipts, byte[] Rlp)>(_deferredWriter, WriteReceipts, _writeLock);
                // Fsync the whole receipts DB WAL (receipts + transaction columns) after the barrier drains the writer.
                (persistenceBarrier ?? NullStatePersistenceBarrier.Instance).RegisterFlush(() => _database.Flush(onlyWal: true));
            }
        }

        private void BlockTreeOnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            if (_deferredWriter is null)
            {
                EnsureCanonical(e.Block);
                NewCanonicalReceipts?.Invoke(this, e);

                // Don't block the main loop
                Task.Run(() =>
                {
                    PruneOldTxIndex(e.Block);
                });
                return;
            }

            Block block = e.Block;

            // Capture the lookup-limit horizon now; at dequeue time queue lag would skip near-horizon indexes.
            ulong lastBlockNumber = _blockTree.FindBestSuggestedHeader()?.Number ?? 0UL;

            bool shouldIndex = ShouldIndexTxs(block.Number, lastBlockNumber);
            if (shouldIndex)
            {
                PendingTxIndexValue pending = _receiptConfig.CompactTxIndex
                    ? new PendingTxIndexValue(block.Number, null)
                    : new PendingTxIndexValue(0, block.Hash);
                foreach (Transaction tx in block.Transactions)
                {
                    tx.Hash ??= tx.CalculateHash();
                    _pendingTxIndex[tx.Hash] = pending;
                }

                // Publish the cancellation ledger and enqueue BEFORE the event, so a state persist that
                // observes the block always drains this. FIFO order keeps a reorg remap and the prune correct.
                PendingCanonicalEntry canonical = new(block, lastBlockNumber, pending);
                _pendingCanonical[block.Hash!.ValueHash256] = canonical;
                _deferredWriter.Enqueue(() => PersistDeferredCanonical(canonical));
            }
            else
            {
                _deferredWriter.Enqueue(() => PruneOldTxIndex(block));
            }

            NewCanonicalReceipts?.Invoke(this, e);
        }

        private void PersistDeferredCanonical(PendingCanonicalEntry entry)
        {
            lock (_writeLock)
            {
                // Skip if RemoveReceipts cancelled this block; writing would resurrect its tx-index. Reference-conditional.
                ValueHash256 key = entry.Block.Hash!.ValueHash256;
                if (!_pendingCanonical.TryGetValue(key, out PendingCanonicalEntry? current) || !ReferenceEquals(current, entry))
                {
                    return;
                }

                EnsureCanonical(entry.Block, entry.LastBlockNumber);
                foreach (Transaction tx in entry.Block.Transactions)
                {
                    _pendingTxIndex.TryRemove(new KeyValuePair<ValueHash256, PendingTxIndexValue>(tx.Hash!, entry.TxIndexValue));
                }
                PruneOldTxIndex(entry.Block);

                _pendingCanonical.TryRemove(new KeyValuePair<ValueHash256, PendingCanonicalEntry>(key, entry));
            }
        }

        private void PruneOldTxIndex(Block newMain)
        {
            if (_receiptConfig.TxLookupLimit > 0ul && newMain.Number > _receiptConfig.TxLookupLimit.Value)
            {
                Block newOldTx = _blockTree.FindBlock(newMain.Number - _receiptConfig.TxLookupLimit.Value);
                if (newOldTx is not null)
                {
                    RemoveBlockTx(newOldTx);
                }
            }
        }

        /// <summary>Mirrors the skip conditions of <see cref="EnsureCanonical(Block, ulong?)"/>.</summary>
        private bool ShouldIndexTxs(ulong blockNumber, ulong lastBlockNumber)
        {
            if (_receiptConfig.TxLookupLimit == ulong.MaxValue) return false;
            if (_receiptConfig.TxLookupLimit != 0ul && lastBlockNumber >= _receiptConfig.TxLookupLimit.Value && blockNumber <= lastBlockNumber - _receiptConfig.TxLookupLimit.Value) return false;
            return true;
        }

        public Hash256 FindBlockHash(Hash256 txHash)
        {
            if (_pendingTxIndex.TryGetValue(txHash, out PendingTxIndexValue pending))
            {
                // Number-valued entries re-resolve canonically like the persisted form, so a reorged-out block misses here too.
                return pending.BlockHash ?? _blockTree.FindBlockHash(pending.BlockNumber);
            }

            byte[] blockHashData = _transactionDb.Get(txHash);
            if (blockHashData is null) return FindReceiptObsolete(txHash)?.BlockHash;

            if (blockHashData.Length == Hash256.Size) return new Hash256(blockHashData);

            ulong blockNum = new RlpReader(blockHashData).DecodeULong();
            return _blockTree.FindBlockHash(blockNum);
        }

        // Find receipt stored with old - obsolete format.
        private TxReceipt FindReceiptObsolete(Hash256 hash)
        {
            Span<byte> receiptData = _defaultColumn.GetSpan(hash);
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

            // Pending entries are already sender-recovered; served until the deferred write lands.
            if (_pendingReceipts is not null && _pendingReceipts.TryGet(blockHash, out (TxReceipt[] Receipts, byte[] Rlp) pending))
            {
                return pending.Receipts;
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
        private unsafe Span<byte> GetReceiptData(ulong blockNumber, Hash256 blockHash)
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

        private static void GetBlockNumPrefixedKey(ulong blockNumber, Hash256 blockHash, Span<byte> output)
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

        public bool CanGetReceiptsByHash(ulong blockNumber) => blockNumber >= MigratedBlockNumber;

        public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            if (_receiptsCache.TryGet(blockHash, out TxReceipt[] receipts))
            {
                iterator = new ReceiptsIterator(receipts);
                return true;
            }

            // eth_getLogs reads receipts through this iterator; without this arm an evicted, unflushed block returns no logs.
            if (_pendingReceipts is not null && _pendingReceipts.TryGet(blockHash, out (TxReceipt[] Receipts, byte[] Rlp) pending))
            {
                iterator = new ReceiptsIterator(pending.Receipts);
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

        public void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical = true, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null)
            => Insert(block, txReceipts, _specProvider.GetSpec(block.Header), ensureCanonical, writeFlags, lastBlockNumber);

        [SkipLocalsInit]
        public void Insert(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec, bool ensureCanonical = true, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null)
        {
            InsertCore(block, txReceipts, spec, ensureCanonical, writeFlags, lastBlockNumber);

            if (block.Number < MigratedBlockNumber)
            {
                MigratedBlockNumber = block.Number;
            }
        }

        void IReceiptMigrationStore.InsertForMigration(Block block, TxReceipt[] receipts)
            => InsertCore(block, receipts, _specProvider.GetSpec(block.Header), ensureCanonical: true, WriteFlags.None, lastBlockNumber: null);

        public void InsertDeferred(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec)
        {
            if (_pendingReceipts is null)
            {
                Insert(block, txReceipts, spec, ensureCanonical: false);
                return;
            }

            txReceipts ??= [];
            if (block.Transactions.Length != txReceipts.Length)
            {
                throw new InvalidDataException(
                    $"Block {block.ToString(Block.Format.FullHashAndNumber)} has different numbers " +
                    $"of transactions {block.Transactions.Length} and receipts {txReceipts.Length}.");
            }

            // Everything visibility-relevant is synchronous (recovery, encode, cache, overlay, watermark, event); only the DB write defers.
            _receiptsRecovery.TryRecover(block, txReceipts, false);

            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;
            byte[] rlp;
            using (ArrayPoolSpan<byte> encoded = _storageDecoder.EncodeToArrayPoolSpan(txReceipts, behaviors))
            {
                rlp = ((ReadOnlySpan<byte>)encoded).ToArray();
            }

            Hash256 blockHash = block.Hash!;
            _receiptsCache.Set(blockHash, txReceipts);
            _pendingReceipts.Publish(block.Number, blockHash, (txReceipts, rlp));

            if (block.Number < MigratedBlockNumber)
            {
                MigratedBlockNumber = block.Number;
            }

            ReceiptsInserted?.Invoke(this, new(block.Header, txReceipts));
        }

        [SkipLocalsInit]
        private void WriteReceipts(ulong blockNumber, Hash256 blockHash, (TxReceipt[] Receipts, byte[] Rlp) payload)
        {
            Span<byte> blockNumPrefixed = stackalloc byte[40];
            GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);
            _receiptsDb.PutSpan(blockNumPrefixed, payload.Rlp, WriteFlags.None);
        }

        [SkipLocalsInit]
        private void InsertCore(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec, bool ensureCanonical, WriteFlags writeFlags, ulong? lastBlockNumber)
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

            ulong blockNumber = block.Number;
            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;

            using ArrayPoolSpan<byte> rlp = _storageDecoder.EncodeToArrayPoolSpan(txReceipts, behaviors);
            Span<byte> blockNumPrefixed = stackalloc byte[40];
            GetBlockNumPrefixedKey(blockNumber, block.Hash!, blockNumPrefixed);

            _receiptsDb.PutSpan(blockNumPrefixed, rlp, writeFlags);

            _receiptsCache.Set(block.Hash, txReceipts);

            if (ensureCanonical)
            {
                EnsureCanonical(block, lastBlockNumber);
            }

            ReceiptsInserted?.Invoke(this, new(block.Header, txReceipts));
        }

        public ulong MigratedBlockNumber
        {
            get => _migratedBlockNumber;
            set
            {
                _migratedBlockNumber = value;
                _defaultColumn.PutSpan(MigrationBlockNumberKey.Bytes, value.ToBigEndianSpanWithoutLeadingZeros(out _));
            }
        }

        internal void ClearCache() => _receiptsCache.Clear();

        [SkipLocalsInit]
        public bool HasBlock(ulong blockNumber, Hash256 blockHash)
        {
            if (_receiptsCache.Contains(blockHash)) return true;
            if (_pendingReceipts?.Contains(blockHash) == true) return true;

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

        public void EnsureCanonical(Block block) => EnsureCanonical(block, null);

        public void RemoveReceipts(Block block)
        {
            // Only production caller is ancient-history pruning. Under deferral the removal runs under the
            // shared lock (via the overlay) so a queued write cannot interleave and resurrect the data.
            if (_pendingReceipts is not null)
            {
                _pendingReceipts.Remove(block.Hash!, () => RemoveReceiptsCore(block));
            }
            else
            {
                RemoveReceiptsCore(block);
            }
        }

        [SkipLocalsInit]
        private void RemoveReceiptsCore(Block block)
        {
            // Cancel any queued canonical write for this block so it cannot resurrect the tx-index below.
            _pendingCanonical.TryRemove(block.Hash!.ValueHash256, out _);

            foreach (Transaction tx in block.Transactions)
            {
                if (tx.Hash is not null && _pendingTxIndex.TryGetValue(tx.Hash, out PendingTxIndexValue pending) &&
                    (pending.BlockHash is null ? pending.BlockNumber == block.Number : pending.BlockHash == block.Hash))
                {
                    _pendingTxIndex.TryRemove(new KeyValuePair<ValueHash256, PendingTxIndexValue>(tx.Hash, pending));
                }
            }

            _receiptsCache.Delete(block.Hash);

            Span<byte> blockNumPrefixed = stackalloc byte[40];
            GetBlockNumPrefixedKey(block.Number, block.Hash, blockNumPrefixed);
            _receiptsDb.Remove(blockNumPrefixed);

            RemoveBlockTx(block);
        }

        private void RemoveBlockTx(Block block)
        {
            using IWriteBatch writeBatch = _transactionDb.StartWriteBatch();
            foreach (Transaction tx in block.Transactions)
            {
                writeBatch[tx.Hash.Bytes] = null;
            }
        }

        private void EnsureCanonical(Block block, ulong? lastBlockNumber)
        {
            using IWriteBatch writeBatch = _transactionDb.StartWriteBatch();

            lastBlockNumber ??= _blockTree.FindBestSuggestedHeader()?.Number ?? 0UL;

            if (!ShouldIndexTxs(block.Number, lastBlockNumber.Value)) return;
            if (_receiptConfig.CompactTxIndex)
            {
                byte[] blockNumber = Rlp.Encode(block.Number).Bytes;
                foreach (Transaction tx in block.Transactions)
                {
                    tx.Hash ??= tx.CalculateHash();
                    Hash256 hash = tx.Hash;
                    writeBatch[hash.Bytes] = blockNumber;
                }
            }
            else
            {
                byte[] blockHash = block.Hash.BytesToArray();
                foreach (Transaction tx in block.Transactions)
                {
                    tx.Hash ??= tx.CalculateHash();
                    Hash256 hash = tx.Hash;
                    writeBatch[hash.Bytes] = blockHash;
                }
            }
        }
    }
}
