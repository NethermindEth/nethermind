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
        private readonly IStatePersistenceBarrier _persistenceBarrier;

        // Pending overlays: source of truth for data whose durable write is still queued. Published
        // synchronously before any event; removed only after the DB write, value-conditionally. Never evict.
        private readonly ConcurrentDictionary<ValueHash256, PendingReceiptEntry> _pendingReceipts = new();
        private readonly ConcurrentDictionary<ValueHash256, PendingTxIndexValue> _pendingTxIndex = new();
        // Per-block ledger so the barrier gate can make a block's canonical tx-index durable before its
        // state: else a crash strands persisted state whose tx lookups are permanently lost (not re-derived).
        private readonly ConcurrentDictionary<ValueHash256, PendingCanonicalEntry> _pendingCanonical = new();

        /// <summary>
        /// A receipt set whose durable write is still queued. Carries the block number so the state
        /// persistence barrier can force-flush every entry up to a given block, and the pre-encoded RLP
        /// so the flush never re-encodes. A plain class (not a record) so value-conditional removal keys
        /// on reference identity - a re-insert of the same receipts keeps its own distinct entry.
        /// </summary>
        private sealed class PendingReceiptEntry(ulong blockNumber, Hash256 blockHash, TxReceipt[] receipts, byte[] rlp)
        {
            public ulong BlockNumber { get; } = blockNumber;
            public Hash256 BlockHash { get; } = blockHash;
            public TxReceipt[] Receipts { get; } = receipts;
            public byte[] Rlp { get; } = rlp;
        }

        /// <summary>
        /// A canonical tx-index write still queued for a block. Carries the block and the synchronously
        /// captured lookup-limit horizon so the write (and the barrier-gated flush) index exactly the
        /// same transactions the live event decided to. A plain class so removal keys on reference identity.
        /// </summary>
        private sealed class PendingCanonicalEntry(ulong blockNumber, Block block, ulong lastBlockNumber, PendingTxIndexValue txIndexValue, long sequence)
        {
            public ulong BlockNumber { get; } = blockNumber;
            public Block Block { get; } = block;
            public ulong LastBlockNumber { get; } = lastBlockNumber;
            public PendingTxIndexValue TxIndexValue { get; } = txIndexValue;
            // Publication order; the barrier flushes canonical entries in this order so a reorg's remap
            // and the sequential prune land the same way the writer's FIFO would.
            public long Sequence { get; } = sequence;
        }

        private long _canonicalSequence;

        // Serialises a queued background write against a synchronous removal of the same data, so a
        // write cannot land after a delete and resurrect it. Uncontended except when a removal races
        // an in-flight flush.
        private readonly Lock _writeLock = new();

        /// <summary>
        /// Mirrors exactly what the deferred tx-index database write will contain: the block number
        /// under <see cref="IReceiptConfig.CompactTxIndex"/> (resolved canonically on read, so a
        /// reorged-out block self-heals just like the persisted form), the block hash otherwise.
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
            _persistenceBarrier = persistenceBarrier ?? NullStatePersistenceBarrier.Instance;
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

            // The eager writer keeps the overlay shallow; this gate guarantees any straggler is durable
            // before the block's state is. No-op when deferral or the barrier is disabled.
            if (_deferredWriter is not null && _persistenceBarrier.IsEnabled)
            {
                _persistenceBarrier.Register(FlushBlockDataUpTo);
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

            // The lookup-limit horizon must be captured now: computed at writer-dequeue time it would
            // run ahead by the queue lag and silently skip near-horizon indexes during fast replay.
            ulong lastBlockNumber = _blockTree.FindBestSuggestedHeader()?.Number ?? 0UL;

            bool shouldIndex = ShouldIndexTxs(block.Number, lastBlockNumber);
            PendingTxIndexValue pending = default;
            PendingCanonicalEntry? canonical = null;
            if (shouldIndex)
            {
                pending = _receiptConfig.CompactTxIndex
                    ? new PendingTxIndexValue(block.Number, null)
                    : new PendingTxIndexValue(0, block.Hash);
                foreach (Transaction tx in block.Transactions)
                {
                    tx.Hash ??= tx.CalculateHash();
                    _pendingTxIndex[tx.Hash] = pending;
                }

                // Publish the canonical ledger entry before the event and enqueue, so the barrier gate can
                // force this block's tx-index durable before its state persists (see _pendingCanonical).
                canonical = new PendingCanonicalEntry(block.Number, block, lastBlockNumber, pending, Interlocked.Increment(ref _canonicalSequence));
                _pendingCanonical[block.Hash!.ValueHash256] = canonical;
            }

            // Fired before enqueueing the durable write, matching today's order (event, then the
            // background prune); by this point the pending index already serves FindBlockHash.
            NewCanonicalReceipts?.Invoke(this, e);

            if (canonical is not null)
            {
                _deferredWriter.Enqueue(() => PersistDeferredCanonical(canonical));
            }
            else
            {
                _deferredWriter.Enqueue(() => PruneOldTxIndex(block));
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
                // Number-valued entries re-resolve canonically exactly like the persisted compact
                // form, so a reorged-out block misses here just as it would after the flush.
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
            if (_pendingReceipts.TryGetValue(blockHash, out PendingReceiptEntry? pending))
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

            // eth_getLogs reaches receipts through this iterator, not Get - without this arm an
            // unflushed block whose LRU entry was evicted would silently return no logs.
            if (_pendingReceipts.TryGetValue(blockHash, out PendingReceiptEntry? pending))
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
            if (_deferredWriter is null)
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

            // Everything visibility-relevant is synchronous (recovery, immutable RLP encode, cache +
            // overlay publish, migration watermark, insertion event); only the database write defers.
            _receiptsRecovery.TryRecover(block, txReceipts, false);

            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;
            byte[] rlp;
            using (ArrayPoolSpan<byte> encoded = _storageDecoder.EncodeToArrayPoolSpan(txReceipts, behaviors))
            {
                rlp = ((ReadOnlySpan<byte>)encoded).ToArray();
            }

            Hash256 blockHash = block.Hash!;
            PendingReceiptEntry entry = new(block.Number, blockHash, txReceipts, rlp);
            _receiptsCache.Set(blockHash, txReceipts);
            _pendingReceipts[blockHash] = entry;

            if (block.Number < MigratedBlockNumber)
            {
                MigratedBlockNumber = block.Number;
            }

            ReceiptsInserted?.Invoke(this, new(block.Header, txReceipts));

            _deferredWriter.Enqueue(() => PersistDeferredReceipts(entry));
        }

        [SkipLocalsInit]
        private bool PersistDeferredReceipts(PendingReceiptEntry entry)
        {
            lock (_writeLock)
            {
                // Skip if a removal (or the gate racing the writer) already dropped this exact entry; the
                // lock makes check-then-write atomic against RemoveReceipts so a write cannot resurrect data.
                ValueHash256 key = entry.BlockHash.ValueHash256;
                if (!_pendingReceipts.TryGetValue(key, out PendingReceiptEntry? current) || !ReferenceEquals(current, entry))
                {
                    return false;
                }

                Span<byte> blockNumPrefixed = stackalloc byte[40];
                GetBlockNumPrefixedKey(entry.BlockNumber, entry.BlockHash, blockNumPrefixed);
                _receiptsDb.PutSpan(blockNumPrefixed, entry.Rlp, WriteFlags.None);

                _pendingReceipts.TryRemove(new KeyValuePair<ValueHash256, PendingReceiptEntry>(key, entry));
                return true;
            }
        }

        private bool PersistDeferredCanonical(PendingCanonicalEntry entry)
        {
            lock (_writeLock)
            {
                // Reference-conditional against a synchronous removal or a re-add, matching the receipts path.
                ValueHash256 key = entry.Block.Hash!.ValueHash256;
                if (!_pendingCanonical.TryGetValue(key, out PendingCanonicalEntry? current) || !ReferenceEquals(current, entry))
                {
                    return false;
                }

                EnsureCanonical(entry.Block, entry.LastBlockNumber);
                foreach (Transaction tx in entry.Block.Transactions)
                {
                    _pendingTxIndex.TryRemove(new KeyValuePair<ValueHash256, PendingTxIndexValue>(tx.Hash!, entry.TxIndexValue));
                }
                PruneOldTxIndex(entry.Block);

                _pendingCanonical.TryRemove(new KeyValuePair<ValueHash256, PendingCanonicalEntry>(key, entry));
                return true;
            }
        }

        /// <summary>
        /// Barrier hook: persist any queued receipt and canonical tx-index write for a block up to
        /// <paramref name="blockNumber"/>, then fsync the receipts DB WAL (one fsync covers both columns).
        /// </summary>
        /// <remarks>
        /// The fsync is unconditional: the eager writer's writes are WAL-buffered (WriteFlags.None) and
        /// nothing else syncs them, so gating on the gate winning the write would leave the common path unsynced.
        /// </remarks>
        private void FlushBlockDataUpTo(long blockNumber)
        {
            ulong upTo = (ulong)blockNumber;
            foreach (KeyValuePair<ValueHash256, PendingReceiptEntry> kv in _pendingReceipts)
            {
                if (kv.Value.BlockNumber <= upTo)
                {
                    PersistDeferredReceipts(kv.Value);
                }
            }

            // Canonical tx-index writes are order-sensitive (a reorg remaps the same tx across blocks and
            // pruning is sequential), so flush them in publication order rather than dictionary order.
            List<PendingCanonicalEntry> canonical = [];
            foreach (KeyValuePair<ValueHash256, PendingCanonicalEntry> kv in _pendingCanonical)
            {
                if (kv.Value.BlockNumber <= upTo) canonical.Add(kv.Value);
            }
            canonical.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
            foreach (PendingCanonicalEntry entry in canonical)
            {
                PersistDeferredCanonical(entry);
            }

            // Fsync at the columns-DB level (a per-column Flush would force a full memtable flush).
            _database.Flush(onlyWal: true);
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
            if (_pendingReceipts.ContainsKey(blockHash)) return true;

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
            // Stays fully synchronous (only production caller is history pruning of ancient blocks).
            // Under the write lock so a queued deferred write cannot land between the overlay removal
            // and the database delete and resurrect the data.
            lock (_writeLock)
            {
                _pendingReceipts.TryRemove(block.Hash!, out _);
                _pendingCanonical.TryRemove(block.Hash!, out _);
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
