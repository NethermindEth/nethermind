// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using Timer = System.Timers.Timer;

namespace Nethermind.Init.Steps.Migrations
{
    public class ReceiptMigration : IDatabaseMigration, IReceiptsMigration
    {
        private readonly ILogger _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        internal Task? _migrationTask;

        private readonly ProgressLogger _progressLogger;
        [NotNull]
        private readonly IReceiptMigrationStore? _migrationStore;
        [NotNull]
        private readonly IBlockTree? _blockTree;
        [NotNull]
        private readonly ISyncModeSelector? _syncModeSelector;
        [NotNull]
        private readonly IChainLevelInfoRepository? _chainLevelInfoRepository;

        private readonly IReceiptConfig _receiptConfig;
        private readonly IColumnsDb<ReceiptsColumns> _receiptsDb;
        private readonly IDb _txIndexDb;
        private readonly IDb _receiptsBlockDb;
        private readonly IReceiptsRecovery _recovery;

        public ReceiptMigration(
            IReceiptMigrationStore migrationStore,
            IBlockTree blockTree,
            ISyncModeSelector syncModeSelector,
            IChainLevelInfoRepository chainLevelInfoRepository,
            IReceiptConfig receiptConfig,
            IColumnsDb<ReceiptsColumns> receiptsDb,
            IReceiptsRecovery recovery,
            ILogManager logManager
        )
        {
            _migrationStore = migrationStore ?? throw new StepDependencyException(nameof(migrationStore));
            _blockTree = blockTree ?? throw new StepDependencyException(nameof(blockTree));
            _syncModeSelector = syncModeSelector ?? throw new StepDependencyException(nameof(syncModeSelector));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new StepDependencyException(nameof(chainLevelInfoRepository));
            _receiptConfig = receiptConfig ?? throw new StepDependencyException("receiptConfig");
            _receiptsDb = receiptsDb;
            _receiptsBlockDb = _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);
            _txIndexDb = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
            _recovery = recovery;
            _logger = logManager.GetClassLogger<ReceiptMigration>();
            _progressLogger = new ProgressLogger("Receipts migration", logManager);
        }

        // Actually start running it.
        public async Task<bool> Run(ulong from, ulong to)
        {
            _cancellationTokenSource?.Cancel();
            try
            {
                await (_migrationTask ?? Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _migrationTask = Task.Run(async () =>
            {
                await _syncModeSelector.WaitUntilMode(CanMigrate, _cancellationTokenSource.Token);
                RunMigration(from, to, false, _cancellationTokenSource.Token);
            });
            return _receiptConfig.StoreReceipts && _receiptConfig.ReceiptsMigration;
        }
        public async Task Run(CancellationToken cancellationToken)
        {
            if (_receiptConfig.StoreReceipts)
            {
                if (_receiptConfig.ReceiptsMigration)
                {
                    ResetMigrationIndexIfNeeded();
                    await _syncModeSelector.WaitUntilMode(CanMigrate, cancellationToken);
                    RunIfNeeded(cancellationToken);
                }
            }
        }

        private static bool CanMigrate(SyncMode syncMode) => syncMode.NotSyncing();

        private void RunIfNeeded(CancellationToken cancellationToken)
        {
            // Note, it start in decreasing order from this high number.
            ulong migrateToBlockNumber = 0;
            if (_migrationStore.MigratedBlockNumber == ulong.MaxValue)
            {
                migrateToBlockNumber = _syncModeSelector.Current.NotSyncing()
                    ? _blockTree.Head?.Number ?? 0UL
                    : _blockTree.BestKnownNumber;
            }
            else if (_migrationStore.MigratedBlockNumber > 0)
            {
                migrateToBlockNumber = _migrationStore.MigratedBlockNumber - 1;
            }

            if (migrateToBlockNumber > 0)
            {
                try
                {
                    RunMigration(0, migrateToBlockNumber, true, cancellationToken);
                }
                catch (Exception e)
                {
                    _logger.Error("Error running receipt migration", e);
                }
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"ReceiptsDb migration not needed. {migrateToBlockNumber} {_migrationStore.MigratedBlockNumber}");
            }
        }

        private void RunMigration(ulong from, ulong to, bool updateReceiptMigrationPointer, CancellationToken token)
        {
            from = Math.Min(from, to);
            ulong synced = 0;
            long missingBlockBodies = 0;

            if (_logger.IsWarn) _logger.Warn($"Running migration from {from} to {to}");

            _progressLogger.Reset(synced, to - from + 1);

            using Timer timer = new(1000);
            timer.Enabled = true;
            timer.Elapsed += (_, _) =>
            {
                _progressLogger.LogProgress();
            };

            try
            {
                int parallelism = _receiptConfig.ReceiptsMigrationDegreeOfParallelism;
                if (parallelism == 0)
                {
                    parallelism = Environment.ProcessorCount;
                }

                MigrationPointerTracker? pointerTracker = updateReceiptMigrationPointer
                    ? new MigrationPointerTracker(_migrationStore, to, parallelism)
                    : null;

                GetBlockBodiesForMigration(from, to, pointerTracker, token)
                    .AsParallel().WithDegreeOfParallelism(parallelism).ForAll((item) =>
                {
                    (ulong blockNum, Hash256 blockHash) = item;
                    Block? storedBlock = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
                    _progressLogger.Update(Interlocked.Increment(ref synced));
                    if (storedBlock is null)
                    {
                        Interlocked.Increment(ref missingBlockBodies);
                        if (_logger.IsDebug) _logger.Debug($"Block {blockNum} not found. Logs will not be searchable for this block.");
                        pointerTracker?.ReportCompleted(blockNum);
                        return;
                    }

                    bool migrationCompleted = MigrateBlock(storedBlock);
                    if (migrationCompleted)
                    {
                        pointerTracker?.ReportCompleted(blockNum);
                    }
                    else
                    {
                        pointerTracker?.ReportIncomplete(blockNum);
                    }
                });

                if (missingBlockBodies > 0 && _logger.IsWarn)
                {
                    _logger.Warn($"Receipt migration skipped {missingBlockBodies} blocks with missing bodies; their legacy receipt data was left unchanged.");
                }

                if (!token.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info("Compacting receipts database");
                    _receiptsDb.Compact();
                    if (_logger.IsInfo) _logger.Info("Compacting receipts tx index database");
                    _txIndexDb.Compact();
                    if (_logger.IsInfo) _logger.Info("Compacting receipts block database");
                    _receiptsBlockDb.Compact();
                }
            }
            finally
            {
                _progressLogger.MarkEnd();
                timer.Stop();
            }

            if (!token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info("Receipt migration finished");
            }
        }

        IEnumerable<(ulong, Hash256)> GetBlockBodiesForMigration(ulong from, ulong to, MigrationPointerTracker? pointerTracker, CancellationToken token)
        {
            bool TryGetMainChainBlockHashFromLevel(ulong number, out Hash256? blockHash)
            {
                using BatchWrite batch = _chainLevelInfoRepository.StartBatch();
                ChainLevelInfo? level = _chainLevelInfoRepository.LoadLevel(number);
                if (level is not null)
                {
                    if (!level.HasBlockOnMainChain)
                    {
                        if (level.BlockInfos.Length > 0)
                        {
                            level.HasBlockOnMainChain = true;
                            _chainLevelInfoRepository.PersistLevel(number, level, batch);
                        }
                    }

                    blockHash = level.MainChainBlock?.BlockHash;
                    return blockHash is not null;
                }
                else
                {
                    blockHash = null;
                    return false;
                }
            }

            if (to >= from)
            {
                for (ulong i = to; ; i--)
                {
                    if (token.IsCancellationRequested)
                    {
                        if (_logger.IsInfo) _logger.Info("Receipt migration cancelled");
                        yield break;
                    }

                    if (TryGetMainChainBlockHashFromLevel(i, out Hash256? blockHash))
                    {
                        yield return (i, blockHash!);
                    }
                    else
                    {
                        pointerTracker?.ReportCompleted(i);
                    }

                    if (i == from)
                    {
                        break;
                    }
                }
            }
        }

        private bool MigrateBlock(Block block)
        {
            Hash256 blockHash = block.Hash
                ?? throw new InvalidDataException($"Cannot migrate receipts for block {block.Number} without a block hash.");
            TxReceipt?[] receipts = _migrationStore.GetForMigration(block.Number, blockHash);
            if (receipts.Length == 0) return true;

            if (receipts.Length != block.Transactions.Length)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Block {block.ToString(Block.Format.FullHashAndNumber)} has {receipts.Length} receipts for {block.Transactions.Length} transactions; leaving its legacy receipt data unchanged.");
                return false;
            }

            if (!TryGetCompleteReceipts(receipts, out TxReceipt[]? completeReceipts, out int missingCount))
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Block {block.ToString(Block.Format.FullHashAndNumber)} is missing {missingCount} of {receipts.Length} receipts; leaving its legacy receipt data unchanged.");
                return false;
            }

            if (_recovery.TryRecover(block, completeReceipts, forceRecoverSender: true) == ReceiptsRecoveryResult.Fail)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Could not recover receipt metadata for block {block.ToString(Block.Format.FullHashAndNumber)}; leaving its legacy receipt data unchanged.");
                return false;
            }

            Hash256[] transactionHashes = new Hash256[completeReceipts.Length];
            for (int i = 0; i < completeReceipts.Length; i++)
            {
                if (completeReceipts[i].TxHash is not Hash256 transactionHash)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"Receipt {i} for block {block.ToString(Block.Format.FullHashAndNumber)} has no transaction hash after recovery; leaving its legacy receipt data unchanged.");
                    return false;
                }

                transactionHashes[i] = transactionHash;
            }

            _migrationStore.InsertForMigration(block, completeReceipts);

            // It used to be that the tx index is stored in the default column so we are moving it into transactions column
            {
                using IWriteBatch writeBatch = _receiptsDb.StartWriteBatch().GetColumnBatch(ReceiptsColumns.Default);
                for (int i = 0; i < transactionHashes.Length; i++)
                {
                    writeBatch[transactionHashes[i].Bytes] = null;
                }
            }

            // Receipts are now prefixed with block number.
            _receiptsBlockDb.Delete(blockHash);

            // Guarded: block.Number can transiently exceed head during reorgs.
            ulong? headNumber = _blockTree.Head?.Number;
            bool txIndexExpired = _receiptConfig.TxLookupLimit > 0ul
                                  && headNumber is ulong h
                                  && h > block.Number
                                  && h - block.Number > _receiptConfig.TxLookupLimit.Value;
            bool neverIndexTx = _receiptConfig.TxLookupLimit == ulong.MaxValue;
            if (neverIndexTx || txIndexExpired)
            {
                using IWriteBatch writeBatch = _txIndexDb.StartWriteBatch();
                foreach (Hash256 txHash in transactionHashes)
                {
                    writeBatch[txHash.Bytes] = null;
                }
            }

            return true;
        }

        private static bool TryGetCompleteReceipts(
            TxReceipt?[] receipts,
            [NotNullWhen(true)] out TxReceipt[]? completeReceipts,
            out int missingCount)
        {
            missingCount = 0;
            TxReceipt[] candidates = new TxReceipt[receipts.Length];
            for (int i = 0; i < receipts.Length; i++)
            {
                TxReceipt? receipt = receipts[i];
                if (receipt is null)
                {
                    missingCount++;
                }
                else
                {
                    candidates[i] = receipt;
                }
            }

            completeReceipts = missingCount == 0 ? candidates : null;
            return completeReceipts is not null;
        }

        private void ResetMigrationIndexIfNeeded()
        {
            if (_receiptConfig.ForceReceiptsMigration)
            {
                _migrationStore.MigratedBlockNumber = ulong.MaxValue;
                return;
            }

            if (_migrationStore.MigratedBlockNumber == ulong.MaxValue) return;
            ulong blockNumber = _blockTree.Head?.Number ?? 0UL;
            while (blockNumber > 0)
            {
                ChainLevelInfo? level = _chainLevelInfoRepository.LoadLevel(blockNumber);
                BlockInfo? firstBlockInfo = level?.BlockInfos.FirstOrDefault();
                if (firstBlockInfo is not null)
                {
                    TxReceipt?[] receipts = _migrationStore.GetForMigration(blockNumber, firstBlockInfo.BlockHash);
                    if (receipts.Length > 0)
                    {
                        if (!TryGetCompleteReceipts(receipts, out TxReceipt[]? completeReceipts, out _) ||
                            IsMigrationNeeded(blockNumber, firstBlockInfo.BlockHash, completeReceipts))
                        {
                            _migrationStore.MigratedBlockNumber = ulong.MaxValue;
                        }

                        break;
                    }
                }

                blockNumber--;
            }
        }

        private bool IsMigrationNeeded(ulong blockNumber, Hash256 blockHash, TxReceipt[] receipts)
        {
            if (!_receiptConfig.CompactReceiptStore && _recovery.NeedRecover(receipts))
            {
                return true;
            }

            byte[]? receiptData = _receiptsBlockDb.Get(blockHash.Bytes);
            receiptData ??= _receiptsBlockDb.Get(Bytes.Concat(blockNumber.ToBigEndianByteArray(), blockHash.Bytes));

            if (receiptData is null)
            {
                return true;
            }

            bool isCompactEncoding = ReceiptArrayStorageDecoder.IsCompactEncoding(receiptData!);
            return _receiptConfig.CompactReceiptStore != isCompactEncoding;
        }

        internal sealed class MigrationPointerTracker(IReceiptStorage receiptStorage, ulong to, int expectedBacklog = 16)
        {
            private readonly Lock _lock = new();
            private readonly HashSet<ulong> _completedAwaitingContiguity = new(expectedBacklog);
            private ulong _nextToConfirm = to;
            private ulong? _highestIncomplete;

            public void ReportIncomplete(ulong blockNumber)
            {
                lock (_lock)
                {
                    if (_highestIncomplete is null || blockNumber > _highestIncomplete)
                    {
                        _highestIncomplete = blockNumber;
                    }

                    ulong highestIncomplete = _highestIncomplete.Value;
                    _completedAwaitingContiguity.RemoveWhere(number => number <= highestIncomplete);
                }
            }

            public void ReportCompleted(ulong blockNumber)
            {
                lock (_lock)
                {
                    if (_highestIncomplete is ulong highestIncomplete && blockNumber <= highestIncomplete)
                    {
                        return;
                    }

                    _completedAwaitingContiguity.Add(blockNumber);

                    bool advanced = false;
                    bool reachedGenesis = false;
                    while (_completedAwaitingContiguity.Remove(_nextToConfirm))
                    {
                        advanced = true;
                        if (_nextToConfirm == 0) { reachedGenesis = true; break; }
                        _nextToConfirm--;
                    }

                    if (!advanced) return;
                    ulong migratedBlockNumber = reachedGenesis ? 0UL : _nextToConfirm + 1;
                    if (receiptStorage.MigratedBlockNumber > migratedBlockNumber)
                    {
                        receiptStorage.MigratedBlockNumber = migratedBlockNumber;
                    }
                }
            }
        }

    }
}
