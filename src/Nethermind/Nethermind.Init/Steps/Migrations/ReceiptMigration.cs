// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using Timer = System.Timers.Timer;

namespace Nethermind.Init.Steps.Migrations
{
    public class ReceiptMigration : IDatabaseMigration, IReceiptsMigration
    {
        private static readonly ObjectPool<Block> EmptyBlock = new DefaultObjectPool<Block>(new EmptyBlockObjectPolicy());

        private readonly ILogger _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        internal Task? _migrationTask;

        private readonly ProgressLogger _progressLogger;
        [NotNull]
        private readonly IReceiptStorage? _receiptStorage;
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

        public ReceiptMigration(IApiWithNetwork api) : this(
            api.ReceiptStorage!,
            api.BlockTree!,
            api.SyncModeSelector!,
            api.ChainLevelInfoRepository!,
            api.Config<IReceiptConfig>(),
            api.DbProvider?.ReceiptsDb!,
            new ReceiptsRecovery(api.EthereumEcdsa, api.SpecProvider),
            api.LogManager
        )
        {
        }

        public ReceiptMigration(
            IReceiptStorage receiptStorage,
            IBlockTree blockTree,
            ISyncModeSelector syncModeSelector,
            IChainLevelInfoRepository chainLevelInfoRepository,
            IReceiptConfig receiptConfig,
            IColumnsDb<ReceiptsColumns> receiptsDb,
            IReceiptsRecovery recovery,
            ILogManager logManager
        )
        {
            _receiptStorage = receiptStorage ?? throw new StepDependencyException(nameof(receiptStorage));
            _blockTree = blockTree ?? throw new StepDependencyException(nameof(blockTree));
            _syncModeSelector = syncModeSelector ?? throw new StepDependencyException(nameof(syncModeSelector));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new StepDependencyException(nameof(chainLevelInfoRepository));
            _receiptConfig = receiptConfig ?? throw new StepDependencyException("receiptConfig");
            _receiptsDb = receiptsDb;
            _receiptsBlockDb = _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);
            _txIndexDb = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
            _recovery = recovery;
            _logger = logManager.GetClassLogger();
            _progressLogger = new ProgressLogger("Receipts migration", logManager);
        }

        // Actually start running it.
        public async Task<bool> Run(long from, long to)
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
            long migrateToBlockNumber = _receiptStorage.MigratedBlockNumber == long.MaxValue
                ? _syncModeSelector.Current.NotSyncing()
                    ? _blockTree.Head?.Number ?? 0
                    : _blockTree.BestKnownNumber
                : _receiptStorage.MigratedBlockNumber - 1;

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
                if (_logger.IsInfo) _logger.Info($"ReceiptsDb migration not needed. {migrateToBlockNumber} {_receiptStorage.MigratedBlockNumber}");
            }
        }

        private void RunMigration(long from, long to, bool updateReceiptMigrationPointer, CancellationToken token)
        {
            from = Math.Min(from, to);
            long synced = 0;

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

                GetBlockBodiesForMigration(from, to, updateReceiptMigrationPointer, token)
                    .AsParallel().WithDegreeOfParallelism(parallelism).ForAll((item) =>
                {
                    (long blockNum, Hash256 blockHash) = item;
                    Block? block = _blockTree.FindBlock(blockHash!, BlockTreeLookupOptions.None);
                    bool usingEmptyBlock = block is null;
                    if (usingEmptyBlock)
                    {
                        block = GetMissingBlock(blockNum, blockHash);
                    }

                    _progressLogger.Update(Interlocked.Increment(ref synced));
                    MigrateBlock(block!);

                    if (usingEmptyBlock)
                    {
                        ReturnMissingBlock(block!);
                    }
                });

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

        Block GetMissingBlock(long i, Hash256? blockHash)
        {
            if (_logger.IsDebug) _logger.Debug($"Block {i} not found. Logs will not be searchable for this block.");
            Block emptyBlock = EmptyBlock.Get();
            emptyBlock.Header.Number = i;
            emptyBlock.Header.Hash = blockHash;
            return emptyBlock;
        }

        static void ReturnMissingBlock(Block emptyBlock)
        {
            EmptyBlock.Return(emptyBlock);
        }

        IEnumerable<(long, Hash256)> GetBlockBodiesForMigration(long from, long to, bool updateReceiptMigrationPointer, CancellationToken token)
        {
            bool TryGetMainChainBlockHashFromLevel(long number, out Hash256? blockHash)
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

            for (long i = to; i >= from; i--)
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

                if (updateReceiptMigrationPointer && _receiptStorage.MigratedBlockNumber > i)
                {
                    _receiptStorage.MigratedBlockNumber = i;
                }
            }
        }

        private void MigrateBlock(Block block)
        {
            TxReceipt?[] receipts = _receiptStorage.Get(block);
            TxReceipt[] notNullReceipts = receipts.Length == 0
                ? []
                : receipts.Where(static r => r is not null).Cast<TxReceipt>().ToArray();

            if (notNullReceipts.Length == 0) return;

            // This should set the new rlp and tx index depending on config.
            _receiptStorage.Insert(block, notNullReceipts);

            // It used to be that the tx index is stored in the default column so we are moving it into transactions column
            {
                using IWriteBatch writeBatch = _receiptsDb.StartWriteBatch().GetColumnBatch(ReceiptsColumns.Default);
                for (int i = 0; i < notNullReceipts.Length; i++)
                {
                    writeBatch[notNullReceipts[i].TxHash!.Bytes] = null;
                }
            }

            // Receipts are now prefixed with block number.
            _receiptsBlockDb.Delete(block.Hash!);

            // Remove old tx index
            bool txIndexExpired = _receiptConfig.TxLookupLimit != 0 && _blockTree.Head?.Number - block.Number > _receiptConfig.TxLookupLimit;
            bool neverIndexTx = _receiptConfig.TxLookupLimit == -1;
            if (neverIndexTx || txIndexExpired)
            {
                using IWriteBatch writeBatch = _txIndexDb.StartWriteBatch();
                foreach (TxReceipt? receipt in notNullReceipts)
                {
                    writeBatch[receipt.TxHash!.Bytes] = null;
                }
            }

            if (notNullReceipts.Length != receipts.Length)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Block {block.ToString(Block.Format.FullHashAndNumber)} is missing {receipts.Length - notNullReceipts.Length} of {receipts.Length} receipts!");
            }
        }

        private void ResetMigrationIndexIfNeeded()
        {
            if (_receiptConfig.ForceReceiptsMigration)
            {
                _receiptStorage.MigratedBlockNumber = long.MaxValue;
                return;
            }

            if (_receiptStorage.MigratedBlockNumber != long.MaxValue)
            {
                long blockNumber = _blockTree.Head?.Number ?? 0;
                while (blockNumber > 0)
                {
                    ChainLevelInfo? level = _chainLevelInfoRepository.LoadLevel(blockNumber);
                    BlockInfo? firstBlockInfo = level?.BlockInfos.FirstOrDefault();
                    if (firstBlockInfo is not null)
                    {
                        TxReceipt[] receipts = _receiptStorage.Get(firstBlockInfo.BlockHash);
                        if (receipts.Length > 0)
                        {
                            if (IsMigrationNeeded(blockNumber, firstBlockInfo.BlockHash, receipts))
                            {
                                _receiptStorage.MigratedBlockNumber = long.MaxValue;
                            }

                            break;
                        }
                    }

                    blockNumber--;
                }
            }
        }

        private bool IsMigrationNeeded(long blockNumber, Hash256 blockHash, TxReceipt[] receipts)
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

        private class EmptyBlockObjectPolicy : IPooledObjectPolicy<Block>
        {
            public Block Create()
            {
                return new Block(new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.Zero, 0L, 0L, 0UL, []));
            }

            public bool Return(Block obj)
            {
                return true;
            }
        }
    }
}
