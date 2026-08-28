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
using Nethermind.Logging;
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
        private readonly IStateHistoryCaptureStatus? _historyCaptureStatus;
        private readonly ILogger _logger;
        private readonly bool _legacyHashKey;

        private const int SweepDeleteSliceSize = 4096;

        /// <summary>Entries examined before cancellation is honoured, so a spent budget costs a walk its tail.</summary>
        private const int SweepMinimumEntriesPerPass = 4096;

        private const int CacheSize = 64;
        private readonly LruCache<ValueHash256, TxReceipt[]> _receiptsCache = new(CacheSize, CacheSize, "receipts");

        private readonly IDeferredBlockDataWriter? _deferredWriter;

        // Read-through overlay of receipts whose write is still queued. Payload = recovered receipts (served
        // to reads) + the RLP behaviors the deferred write encodes with. Encoding runs on the consumer, off
        // the processing path, since reads use the receipts objects and never the bytes. Null when deferral is off.
        private readonly DeferredWriteOverlay<(TxReceipt[] Receipts, RlpBehaviors Behaviors)>? _pendingReceipts;
        // Block-level canonical overlay served to FindBlockHash until the write lands: set on BlockAddedToMain,
        // cleared by the write. A tx lookup scans these lazily, so processing pays nothing per transaction, and
        // this also doubles as the cancellation ledger - RemoveReceipts clears a block's entry so the write skips.
        private readonly ConcurrentDictionary<ValueHash256, PendingCanonicalEntry> _pendingCanonical = new();
        private long _nextCanonicalSequence;

        private sealed class PendingCanonicalEntry(
            PersistentReceiptStorage owner,
            Block block,
            ulong lastBlockNumber,
            PendingTxIndexValue txIndexValue,
            long publicationSequence) : IDeferredWriteOperation
        {
            public Block Block { get; } = block;
            public ulong LastBlockNumber { get; } = lastBlockNumber;
            public PendingTxIndexValue TxIndexValue { get; } = txIndexValue;
            public long PublicationSequence { get; } = publicationSequence;

            public void Execute() => owner.PersistDeferredCanonical(this);
        }

        private sealed class PruneTxIndexOperation(PersistentReceiptStorage owner, Block block) : IDeferredWriteOperation
        {
            public void Execute() => owner.PruneOldTxIndex(block);
        }

        // Serialises the queued receipts write, the canonical-index write, and a synchronous removal. Shared with _pendingReceipts.
        private readonly Lock _writeLock = new();

        // Bodies skipped by StoresBodies, held until capture durably covers their block so a capture breakdown can
        // persist them before the pending state persist prunes the blocks' snapshots — their only other source.
        // Null when derivation is off or capture status is unwired, since then no body is ever skipped.
        private readonly ConcurrentDictionary<ValueHash256, RetainedBody>? _retainedBodies;
        private long _retainedBytes;

        // A block count alone does not bound memory: a block's receipts run from ~100 KB to ~5 MB.
        internal const int MaxRetainedBodies = 1024;
        internal const long MaxRetainedBytes = 256L * 1024 * 1024;

        // Deliberate over-estimates: binding a cap early is harmless, holding more than accounted for is not.
        private const int RetainedReceiptOverhead = 512;
        private const int RetainedLogOverhead = 96;
        private const int RetainedTopicBytes = 56;
        private volatile bool _retentionSaturationLogged;

        private sealed record RetainedBody(ulong BlockNumber, Hash256 BlockHash, TxReceipt[] Receipts, RlpBehaviors Behaviors, long EstimatedBytes);

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
            IStatePersistenceBarrier? persistenceBarrier = null,
            ILogManager? logManager = null,
            IStateHistoryCaptureStatus? historyCaptureStatus = null)
        {
            _historyCaptureStatus = historyCaptureStatus;
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
            _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<PersistentReceiptStorage>();

            _migratedBlockNumber = Get(MigrationBlockNumberKey, ulong.MaxValue);

            KeyValuePair<byte[], byte[]>? firstValue = _receiptsDb.GetAll().FirstOrDefault();
            _legacyHashKey = firstValue.HasValue && firstValue.Value.Key is not null && firstValue.Value.Key.Length == Hash256.Size;

            _blockTree.BlockAddedToMain += BlockTreeOnBlockAddedToMain;

            if (_deferredWriter is not null)
            {
                _pendingReceipts = new DeferredWriteOverlay<(TxReceipt[] Receipts, RlpBehaviors Behaviors)>(_deferredWriter, WriteReceipts, _writeLock);
                // Fsync the whole receipts DB WAL (receipts + transaction columns) after the barrier drains the writer.
                (persistenceBarrier ?? NullStatePersistenceBarrier.Instance).RegisterFlush(() => _database.Flush(onlyWal: true));
            }

            if (historyCaptureStatus is not null && receiptConfig.DeriveFromState)
            {
                _retainedBodies = new ConcurrentDictionary<ValueHash256, RetainedBody>();
                historyCaptureStatus.WatermarkAdvanced += EvictRetainedBodies;
                historyCaptureStatus.CaptureDisabled += PersistRetainedBodies;
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

                // Publish the block-level entry and enqueue BEFORE the event, so a state persist that observes
                // the block always drains this. FIFO order keeps a reorg remap and the prune correct.
                PendingCanonicalEntry canonical = new(
                    this,
                    block,
                    lastBlockNumber,
                    pending,
                    Interlocked.Increment(ref _nextCanonicalSequence));
                _pendingCanonical[block.Hash!.ValueHash256] = canonical;
                _deferredWriter.Enqueue(canonical);
            }
            else
            {
                _deferredWriter.Enqueue(new PruneTxIndexOperation(this, block));
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

                bool hasOldBlock = TryGetOldTxIndexBlock(entry.Block, out ReceiptRecoveryBlock oldBlock);
                try
                {
                    WriteDeferredCanonicalBatch(entry, hasOldBlock, ref oldBlock);
                }
                finally
                {
                    if (hasOldBlock) oldBlock.Dispose();
                }

                // Removing the block-level entry drops all its txs from the lazy scan; the durable write above now serves them.
                _pendingCanonical.TryRemove(new KeyValuePair<ValueHash256, PendingCanonicalEntry>(key, entry));
            }
        }

        private void WriteDeferredCanonicalBatch(PendingCanonicalEntry entry, bool hasOldBlock, ref ReceiptRecoveryBlock oldBlock)
        {
            using IColumnsWriteBatch<ReceiptsColumns> batch = _database.StartWriteBatch();
            IWriteBatch transactionBatch = batch.GetColumnBatch(ReceiptsColumns.Transactions);
            EnsureCanonical(entry.Block, entry.LastBlockNumber, transactionBatch);
            if (hasOldBlock && !TryRemoveBlockTx(ref oldBlock, transactionBatch))
            {
                // RemoveBlockTx clears the shared batch on failure, so restore the durability-critical insert.
                EnsureCanonical(entry.Block, entry.LastBlockNumber, transactionBatch);
            }
        }

        private void PruneOldTxIndex(Block newMain)
        {
            if (!TryGetOldTxIndexBlock(newMain, out ReceiptRecoveryBlock oldBlock)) return;

            try
            {
                using IColumnsWriteBatch<ReceiptsColumns> batch = _database.StartWriteBatch();
                IWriteBatch transactionBatch = batch.GetColumnBatch(ReceiptsColumns.Transactions);
                TryRemoveBlockTx(ref oldBlock, transactionBatch);
            }
            finally
            {
                oldBlock.Dispose();
            }
        }

        private bool TryGetOldTxIndexBlock(Block newMain, out ReceiptRecoveryBlock oldBlock)
        {
            oldBlock = default;
            if (_receiptConfig.TxLookupLimit is not > 0ul || newMain.Number <= _receiptConfig.TxLookupLimit.Value)
            {
                return false;
            }

            ulong oldBlockNumber = newMain.Number - _receiptConfig.TxLookupLimit.Value;
            Hash256? oldBlockHash = _blockTree.FindBlockHash(oldBlockNumber);
            if (oldBlockHash is null) return false;

            ReceiptRecoveryBlock? candidate;
            try
            {
                candidate = _blockStore.GetReceiptRecoveryBlock(oldBlockNumber, oldBlockHash);
            }
            catch (RlpException exception)
            {
                WarnMalformedTxIndexPrune(oldBlockNumber, exception);
                return false;
            }

            if (candidate is not { } block) return false;

            oldBlock = block;
            return true;
        }

        private bool TryRemoveBlockTx(ref ReceiptRecoveryBlock block, IWriteBatch writeBatch)
        {
            try
            {
                RemoveBlockTx(ref block, writeBatch);
                return true;
            }
            catch (RlpException exception)
            {
                WarnMalformedTxIndexPrune(block.Number, exception);
                return false;
            }
        }

        private void WarnMalformedTxIndexPrune(ulong blockNumber, RlpException exception)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Skipping transaction-index pruning for block {blockNumber} because its body RLP is malformed: {exception.Message}");
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
            if (!_pendingCanonical.IsEmpty && TryFindPendingBlockHash(txHash, out Hash256? pendingHash))
            {
                return pendingHash;
            }

            byte[] blockHashData = _transactionDb.Get(txHash);
            if (blockHashData is null) return FindReceiptObsolete(txHash)?.BlockHash;

            if (blockHashData.Length == Hash256.Size) return new Hash256(blockHashData);

            ulong blockNum = new RlpReader(blockHashData).DecodeULong();
            return _blockTree.FindBlockHash(blockNum);
        }

        /// <summary>
        /// Lazily scans the block-level pending overlay for a transaction, so the per-tx cost moves off the
        /// processing path onto the rare tx-hash lookup. The highest block number wins, then the latest
        /// publication at the same height, matching FIFO last-writer-wins of the durable index.
        /// </summary>
        private bool TryFindPendingBlockHash(Hash256 txHash, out Hash256? blockHash)
        {
            PendingCanonicalEntry? best = null;
            foreach (KeyValuePair<ValueHash256, PendingCanonicalEntry> kvp in _pendingCanonical)
            {
                PendingCanonicalEntry entry = kvp.Value;
                if (best is not null
                    && (entry.Block.Number < best.Block.Number
                        || (entry.Block.Number == best.Block.Number
                            && entry.PublicationSequence <= best.PublicationSequence))) continue;

                foreach (Transaction tx in entry.Block.Transactions)
                {
                    if ((tx.Hash ?? tx.CalculateHash()) == txHash)
                    {
                        best = entry;
                        break;
                    }
                }
            }

            if (best is null)
            {
                blockHash = null;
                return false;
            }

            // Number-valued entries re-resolve canonically like the persisted form, so a reorged-out block misses here too.
            blockHash = best.TxIndexValue.BlockHash ?? _blockTree.FindBlockHash(best.TxIndexValue.BlockNumber);
            return true;
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
            if (_pendingReceipts is not null && _pendingReceipts.TryGet(blockHash, out (TxReceipt[] Receipts, RlpBehaviors Behaviors) pending))
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
            if (_pendingReceipts is not null && _pendingReceipts.TryGet(blockHash, out (TxReceipt[] Receipts, RlpBehaviors Behaviors) pending))
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
                bool storesBody = StoresBodies(spec);
                InsertCore(block, txReceipts, spec, ensureCanonical: false, WriteFlags.None, lastBlockNumber: null,
                    storeBody: storesBody);
                if (!storesBody) RetainSkippedBody(block, txReceipts ?? []);
                if (block.Number < MigratedBlockNumber) MigratedBlockNumber = block.Number;
                return;
            }

            txReceipts ??= [];
            if (block.Transactions.Length != txReceipts.Length)
            {
                throw new InvalidDataException(
                    $"Block {block.ToString(Block.Format.FullHashAndNumber)} has different numbers " +
                    $"of transactions {block.Transactions.Length} and receipts {txReceipts.Length}.");
            }

            // Everything visibility-relevant is synchronous (recovery, cache, overlay, watermark, event). Encoding
            // and the DB write both defer: reads serve the receipts objects, never the bytes, so the RLP is only
            // needed by the queued write and is produced on the consumer instead of on the processing path.
            _receiptsRecovery.TryRecover(block, txReceipts, false);

            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;

            Hash256 blockHash = block.Hash!;
            _receiptsCache.Set(blockHash, txReceipts);
            if (StoresBodies(spec)) _pendingReceipts.Publish(block.Number, blockHash, (txReceipts, behaviors));
            else RetainSkippedBody(block, txReceipts);

            if (block.Number < MigratedBlockNumber)
            {
                MigratedBlockNumber = block.Number;
            }

            ReceiptsInserted?.Invoke(this, new(block.Header, txReceipts));
        }

        [SkipLocalsInit]
        private void WriteReceipts(ulong blockNumber, Hash256 blockHash, (TxReceipt[] Receipts, RlpBehaviors Behaviors) payload)
        {
            // Runs on the deferred-writer consumer: encode here, off the processing path. The receipts array is
            // immutable after the synchronous recovery in InsertDeferred, so encoding it later is safe.
            using ArrayPoolSpan<byte> rlp = _storageDecoder.EncodeToArrayPoolSpan(payload.Receipts, payload.Behaviors);
            Span<byte> blockNumPrefixed = stackalloc byte[40];
            GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);
            _receiptsDb.PutSpan(blockNumPrefixed, rlp, WriteFlags.None);
        }

        /// <summary>
        /// Whether the block-processing path writes this block's receipt bodies. Pre-EIP-658 receipts carry a
        /// post-transaction state root that re-execution cannot reproduce, so they are stored even when deriving.
        /// </summary>
        /// <remarks>
        /// Only block processing may skip the write, because only its blocks are regenerable. Sync, era import and
        /// receipt migration write through unconditionally — migration in particular deletes the legacy key after
        /// re-inserting, so a skipped write there would destroy the bodies.
        /// </remarks>
        private bool StoresBodies(IReleaseSpec spec) =>
            !_receiptConfig.DeriveFromState
            || !spec.IsEip658Enabled
            // A skipped body is permanently lost once its block leaves the in-memory tier: follow live capture
            // health, and treat absent status (patricia backend) as unhealthy.
            || _historyCaptureStatus?.CaptureHealthy != true
            || IsRetentionSaturated();

        /// <summary>Whether either retention cap is reached — the skip then stops until an eviction drops below it.</summary>
        private bool IsRetentionSaturated()
        {
            if (_retainedBodies is null) return false;

            int count = _retainedBodies.Count;
            long bytes = Volatile.Read(ref _retainedBytes);
            if (count < MaxRetainedBodies && bytes < MaxRetainedBytes) return false;

            if (_logger.IsWarn && !_retentionSaturationLogged)
            {
                _retentionSaturationLogged = true;
                _logger.Warn(
                    $"Retained receipt bodies reached the retention cap ({count}/{MaxRetainedBodies} blocks, {bytes / (1024 * 1024)}/{MaxRetainedBytes / (1024 * 1024)} MB) - history capture is not keeping up, e.g. during a finality stall. Storing bodies to disk until it catches up.");
            }

            return true;
        }

        private void RetainSkippedBody(Block block, TxReceipt[] txReceipts)
        {
            if (_retainedBodies is null || txReceipts.Length == 0) return;

            // Post-EIP-658 by StoresBodies, so the storage encoding is fixed.
            RetainedBody body = new(block.Number, block.Hash!, txReceipts,
                RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage, EstimateRetainedBytes(txReceipts));

            DropRetainedBody(block.Hash!.ValueHash256);
            _retainedBodies[block.Hash!.ValueHash256] = body;
            Interlocked.Add(ref _retainedBytes, body.EstimatedBytes);
            UpdateRetentionMetrics();

            // A disable may have drained between the health check that skipped the write and the add above,
            // which would strand this entry forever.
            if (_historyCaptureStatus?.CaptureHealthy is false) PersistRetainedBodies();
        }

        /// <summary>Discards a retained body, keeping <see cref="_retainedBytes"/> in step. Gauges are left to the
        /// caller so a bulk eviction settles them once.</summary>
        private bool DropRetainedBody(in ValueHash256 blockHash)
        {
            if (_retainedBodies is null || !_retainedBodies.TryRemove(blockHash, out RetainedBody? dropped)) return false;

            Interlocked.Add(ref _retainedBytes, -dropped.EstimatedBytes);
            return true;
        }

        /// <summary>Approximate live heap held by a retained body, for the byte cap (see <see cref="MaxRetainedBytes"/>).</summary>
        private static long EstimateRetainedBytes(TxReceipt[] txReceipts)
        {
            long bytes = 0;
            foreach (TxReceipt receipt in txReceipts)
            {
                bytes += RetainedReceiptOverhead;

                LogEntry[]? logs = receipt.Logs;
                if (logs is null) continue;

                foreach (LogEntry log in logs)
                {
                    bytes += RetainedLogOverhead + (log.Topics?.Length ?? 0) * RetainedTopicBytes + (log.Data?.Length ?? 0);
                }
            }

            return bytes;
        }

        private void EvictRetainedBodies(ulong watermark)
        {
            if (_retainedBodies is null) return;

            foreach (KeyValuePair<ValueHash256, RetainedBody> retained in _retainedBodies)
            {
                if (retained.Value.BlockNumber <= watermark) DropRetainedBody(retained.Key);
            }

            // Count locks every bucket, so sample it once for both the re-arm and the gauges.
            int remaining = _retainedBodies.Count;
            long remainingBytes = Volatile.Read(ref _retainedBytes);

            if (remaining < MaxRetainedBodies && remainingBytes < MaxRetainedBytes)
            {
                _retentionSaturationLogged = false;
            }

            UpdateRetentionMetrics(remaining, remainingBytes);
        }

        /// <summary>Retention state for tests, which cannot assert on the process-wide gauges while running in parallel.</summary>
        internal long RetainedBytes => Volatile.Read(ref _retainedBytes);

        internal int RetainedBodyCount => _retainedBodies?.Count ?? 0;

        private void UpdateRetentionMetrics() =>
            UpdateRetentionMetrics(_retainedBodies?.Count ?? 0, Volatile.Read(ref _retainedBytes));

        private static void UpdateRetentionMetrics(int count, long bytes)
        {
            Metrics.RetainedReceiptBodies = count;
            Metrics.RetainedReceiptBodyBytes = bytes;
        }

        /// <summary>
        /// Persists every retained body — capture stopped, so their blocks will never be derivable from history.
        /// </summary>
        /// <remarks>
        /// Runs before the pending state persist resumes, so the WAL sync makes the bodies durable before that
        /// persist prunes the blocks' snapshots. Must not throw: disabling is one-shot, so nothing re-notifies.
        /// </remarks>
        private void PersistRetainedBodies()
        {
            if (_retainedBodies is null || _retainedBodies.IsEmpty) return;

            int persisted = 0;
            bool allWritten = false;
            try
            {
                lock (_writeLock)
                {
                    foreach (KeyValuePair<ValueHash256, RetainedBody> retained in _retainedBodies)
                    {
                        if (!_retainedBodies.TryRemove(retained.Key, out RetainedBody? body)) continue;
                        // Subtract on removal: a throwing write must not leave the entry gone but its bytes counted.
                        Interlocked.Add(ref _retainedBytes, -body.EstimatedBytes);
                        WriteReceipts(body.BlockNumber, body.BlockHash, (body.Receipts, body.Behaviors));
                        persisted++;
                    }
                }

                allWritten = true;

                // SyncWal rethrows where Flush(onlyWal: true) degrades to a warning; this line carries the
                // durability claim above.
                _database.SyncWal();
                if (_logger.IsWarn) _logger.Warn(
                    $"History capture stopped: persisted the receipt bodies retained for {persisted} block(s) that can no longer be derived from state history.");
            }
            catch (Exception e)
            {
                string failure = allWritten
                    ? $"the write-ahead log sync failed after writing all {persisted} block(s), so none of them is guaranteed durable"
                    : $"writing failed after {persisted} block(s), leaving {_retainedBodies.Count} unwritten";
                if (_logger.IsError) _logger.Error(
                    $"Failed to persist the retained receipt bodies after history capture stopped: {failure} - the affected blocks cannot serve receipts; re-sync receipts to recover them.", e);
            }
            finally
            {
                UpdateRetentionMetrics();
            }
        }

        [SkipLocalsInit]
        private void InsertCore(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec, bool ensureCanonical, WriteFlags writeFlags, ulong? lastBlockNumber, bool storeBody = true)
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

            if (storeBody)
            {
                using ArrayPoolSpan<byte> rlp = _storageDecoder.EncodeToArrayPoolSpan(txReceipts, behaviors);
                Span<byte> blockNumPrefixed = stackalloc byte[40];
                GetBlockNumPrefixedKey(blockNumber, block.Hash!, blockNumPrefixed);

                _receiptsDb.PutSpan(blockNumPrefixed, rlp, writeFlags);
            }

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
            // Cancel any queued canonical write for this block so it cannot resurrect the tx-index below. The
            // block-level entry is the only overlay state, so one removal drops all its txs from the lazy scan.
            _pendingCanonical.TryRemove(block.Hash!.ValueHash256, out _);
            if (DropRetainedBody(block.Hash!.ValueHash256)) UpdateRetentionMetrics();

            _receiptsCache.Delete(block.Hash);

            Span<byte> blockNumPrefixed = stackalloc byte[40];
            GetBlockNumPrefixedKey(block.Number, block.Hash, blockNumPrefixed);
            _receiptsDb.Remove(blockNumPrefixed);

            RemoveBlockTx(block);
        }

        /// <summary>Drops the receipts of every block in <c>[fromInclusive, toExclusive)</c> in one operation. The
        /// transaction index is keyed by hash, so it is left to <see cref="SweepTransactionIndex"/>.</summary>
        public void RemoveReceiptsRange(ulong fromInclusive, ulong toExclusive)
        {
            // _pendingCanonical is deliberately NOT drained: it is a cancellation ledger, not a cache, so clearing it
            // would permanently drop the tx-index write of every block queued near the head.
            if (_pendingReceipts is not null)
            {
                _pendingReceipts.RemoveRange(fromInclusive, toExclusive, () => RemoveReceiptsRangeFromDb(fromInclusive, toExclusive));
            }
            else
            {
                RemoveReceiptsRangeFromDb(fromInclusive, toExclusive);
            }

            _receiptsDb.ReclaimBlockNumberRange(fromInclusive, toExclusive);
        }

        private void RemoveReceiptsRangeFromDb(ulong fromInclusive, ulong toExclusive)
        {
            _receiptsDb.DeleteBlockNumberRange(fromInclusive, toExclusive, "receipts");
            if (fromInclusive < toExclusive) _receiptsCache.Clear();
        }

        [SkipLocalsInit]
        public byte[]? SweepTransactionIndex(ulong retainedFromBlock, byte[]? resumeFrom, int maxEntries, CancellationToken cancellationToken, out int removed)
        {
            removed = 0;
            // Not <= 0: the resume key is counted, so a budget of one returns where it started and stalls there.
            if (retainedFromBlock == 0 || maxEntries <= 1 || _transactionDb is not ISortedKeyValueStore sorted) return null;

            // Both sentinels mean the per-block path never removes anything, so an operator on either has asked for
            // the index to be left alone and master leaves it. Unset is the same promise.
            if (_receiptConfig.TxLookupLimit is not ulong limit || limit == 0 || limit == ulong.MaxValue) return null;

            // Below the TxLookupLimit horizon the per-block path already does this, at no read cost. On shipping
            // defaults the retained window is the wider of the two, so without this the walk never finds anything.
            // A head short of the limit deliberately falls through: the per-block path has not started, and when it
            // does it begins at head - limit and only moves forward, so nothing else ever reclaims what is below.
            ulong head = _blockTree.Head?.Number ?? 0;
            if (head > limit && retainedFromBlock <= head - limit) return null;

            Span<byte> upperBound = stackalloc byte[Hash256.Size + 1];
            upperBound.Fill(0xFF);

            int examined = 0;
            int sliceDeletes = 0;
            byte[]? resumeKey = null;
            IWriteBatch batch = _transactionDb.StartWriteBatch();
            try
            {
                using ISortedView view = sorted.GetViewBetween(resumeFrom ?? ReadOnlySpan<byte>.Empty, upperBound);

                while (view.MoveNext())
                {
                    if (PointsBelow(view.CurrentValue, retainedFromBlock))
                    {
                        batch[view.CurrentKey] = null;
                        removed++;

                        if (++sliceDeletes >= SweepDeleteSliceSize)
                        {
                            CommitSweepSlice(ref batch);
                            sliceDeletes = 0;
                        }
                    }

                    // Re-reads the last key once, cheaper than carrying a successor, and the walk's only allocation.
                    // Only after a minimum slice: running last, a token spent on arrival would otherwise stop this
                    // before it examined anything.
                    if (++examined >= maxEntries
                        || (examined >= SweepMinimumEntriesPerPass && cancellationToken.IsCancellationRequested))
                    {
                        resumeKey = view.CurrentKey.ToArray();
                        break;
                    }
                }
            }
            finally
            {
                lock (_writeLock)
                {
                    batch.Dispose();
                }
            }

            return resumeKey;
        }

        /// <summary>Commits what the walk has accumulated and starts a fresh batch. Sliced because one
        /// multi-thousand-key write stalls every other writer here; taken under the canonical writer's lock.</summary>
        private void CommitSweepSlice(ref IWriteBatch batch)
        {
            lock (_writeLock)
            {
                batch.Dispose();
            }

            batch = _transactionDb.StartWriteBatch();
        }

        /// <summary>Whether an index value names a block at or below the last reclaimed one. Under
        /// <see cref="IReceiptConfig.CompactTxIndex"/> the value is the number, otherwise the hash, and the header
        /// supplies the number - headers never being pruned. A hash that does not resolve is left alone.
        /// The two branches are not the same cost: the number is read from the iterator's own buffer, while the hash
        /// costs a header lookup per entry, so the same pass budget covers far fewer entries.</summary>
        private bool PointsBelow(ReadOnlySpan<byte> value, ulong retainedFromBlock)
        {
            if (value.Length == 0) return false;

            if (value.Length == Hash256.Size)
            {
                return _blockTree.FindHeader(new Hash256(value), BlockTreeLookupOptions.TotalDifficultyNotNeeded)
                    is { Number: ulong number } && number < retainedFromBlock;
            }

            try
            {
                return new RlpReader(value).DecodeULong() < retainedFromBlock;
            }
            catch (RlpException)
            {
                return false;
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

        private static void RemoveBlockTx(ref ReceiptRecoveryBlock block, IWriteBatch writeBatch)
        {
            try
            {
                for (int i = 0; i < block.TransactionCount; i++)
                {
                    Hash256 txHash = block.GetNextTransactionHash();
                    writeBatch[txHash.Bytes] = null;
                }
            }
            catch
            {
                writeBatch.Clear();
                throw;
            }
        }

        private void EnsureCanonical(Block block, ulong? lastBlockNumber)
        {
            using IWriteBatch writeBatch = _transactionDb.StartWriteBatch();
            EnsureCanonical(block, lastBlockNumber, writeBatch);
        }

        private void EnsureCanonical(Block block, ulong? lastBlockNumber, IWriteBatch writeBatch)
        {
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
