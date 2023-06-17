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
        private Stopwatch? _stopwatch;
        private long _toBlock;

        private readonly MeasuredProgress _progress = new MeasuredProgress();
        [NotNull]
        private readonly IReceiptStorage? _receiptStorage;
        [NotNull]
        private readonly DisposableStack? _disposeStack;
        [NotNull]
        private readonly IBlockTree? _blockTree;
        [NotNull]
        private readonly ISyncModeSelector? _syncModeSelector;
        [NotNull]
        private readonly IChainLevelInfoRepository? _chainLevelInfoRepository;

        private readonly ReceiptArrayStorageDecoder _storageDecoder;

        private readonly IReceiptConfig _receiptConfig;
        private readonly IColumnsDb<ReceiptsColumns> _receiptsDb;
        private readonly IDbWithSpan _txIndexDb;
        private readonly IDbWithSpan _receiptsBlockDb;
        private readonly IReceiptsRecovery _recovery;

        public ReceiptMigration(IApiWithNetwork api): this(
            api.ReceiptStorage!,
            api.DisposeStack,
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
            DisposableStack disposeStack,
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
            _disposeStack = disposeStack ?? throw new StepDependencyException(nameof(disposeStack));
            _blockTree = blockTree ?? throw new StepDependencyException(nameof(blockTree));
            _syncModeSelector = syncModeSelector ?? throw new StepDependencyException(nameof(syncModeSelector));
            _chainLevelInfoRepository = chainLevelInfoRepository ?? throw new StepDependencyException(nameof(chainLevelInfoRepository));
            _receiptConfig = receiptConfig ?? throw new StepDependencyException("receiptConfig");
            _receiptsDb = receiptsDb;
            _receiptsBlockDb = _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);
            _txIndexDb = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
            _recovery = recovery;
            _logger = logManager.GetClassLogger();
            _storageDecoder = new ReceiptArrayStorageDecoder(); // it just need to detect the type of the storage
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource?.Cancel();
            await (_migrationTask ?? Task.CompletedTask);
        }

        public async Task<bool> Run(long blockNumber)
        {
            _cancellationTokenSource?.Cancel();
            await (_migrationTask ?? Task.CompletedTask);
            _receiptStorage.MigratedBlockNumber = Math.Min(Math.Max(_receiptStorage.MigratedBlockNumber, blockNumber), (_blockTree.Head?.Number ?? 0) + 1);
            DoRun();
            return _receiptConfig.StoreReceipts && _receiptConfig.ReceiptsMigration;
        }

        public void Run()
        {
            if (_receiptConfig.StoreReceipts)
            {
                if (_receiptConfig.ReceiptsMigration)
                {
                    ResetMigrationIndexIfNeeded();
                    DoRun();
                }
            }
        }

        private void DoRun()
        {
            if (_receiptConfig.StoreReceipts)
            {
                if (CanMigrate(_syncModeSelector.Current))
                {
                    RunMigration();
                }
                else
                {
                    _syncModeSelector.Changed -= OnSyncModeChanged;
                    _syncModeSelector.Changed += OnSyncModeChanged;
                    if (_logger.IsInfo) _logger.Info($"ReceiptsDb migration will start after switching to full sync.");
                }
            }
        }

        private bool CanMigrate(SyncMode syncMode) => syncMode.NotSyncing();

        private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (CanMigrate(e.Current))
            {
                RunMigration();
                _syncModeSelector.Changed -= OnSyncModeChanged;
            }
        }

        private void RunMigration()
        {
            // Note, it start in decreasing order from this high number.
            long migrateToBlockNumber = _receiptStorage.MigratedBlockNumber == long.MaxValue
                    ? _syncModeSelector.Current.NotSyncing()
                        ? _blockTree.Head?.Number ?? 0
                        : _blockTree.BestKnownNumber
                    : _receiptStorage.MigratedBlockNumber - 1;
            _toBlock = migrateToBlockNumber;

            _logger.Warn($"Running migration to {_toBlock}");

            if (_toBlock > 0)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _disposeStack.Push(this);
                _stopwatch = Stopwatch.StartNew();
                _migrationTask = Task.Run(() => RunMigration(_cancellationTokenSource.Token))
                    .ContinueWith(x =>
                    {
                        if (x.IsFaulted && _logger.IsError)
                        {
                            _stopwatch.Stop();
                            _logger.Error(GetLogMessage("failed", $"Error: {x.Exception}"), x.Exception);
                        }
                    });
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"ReceiptsDb migration not needed. {migrateToBlockNumber} {_receiptStorage.MigratedBlockNumber}");
            }
        }

        private void RunMigration(CancellationToken token)
        {
            long synced = 1;

            _progress.Reset(synced);

            if (_logger.IsInfo) _logger.Info(GetLogMessage("started"));

            using Timer timer = new(1000);
            timer.Enabled = true;
            timer.Elapsed += (_, _) =>
            {
                if (_logger.IsInfo) _logger.Info(GetLogMessage("in progress"));
            };

            try
            {
                int parallelism = _receiptConfig.ReceiptsMigrationDegreeOfParallelism;
                if (parallelism == 0)
                {
                    parallelism = Environment.ProcessorCount;
                }

                GetBlockBodiesForMigration(token).AsParallel().WithDegreeOfParallelism(parallelism).ForAll((item) =>
                {
                    (long blockNum, Keccak blockHash) = item;
                    Block? block = _blockTree.FindBlock(blockHash!, BlockTreeLookupOptions.None);
                    bool usingEmptyBlock = block == null;
                    if (usingEmptyBlock) {
                        block = GetMissingBlock(blockNum, blockHash);
                    }

                    _progress.Update(Interlocked.Increment(ref synced));
                    MigrateBlock(block!);

                    if (usingEmptyBlock)
                    {
                        ReturnMissingBlock(block!);
                    }
                });

                if (!token.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info(GetLogMessage("Compacting receipts database"));
                    _receiptsDb.Compact();
                    if (_logger.IsInfo) _logger.Info(GetLogMessage("Compacting receipts tx index database"));
                    _txIndexDb.Compact();
                    if (_logger.IsInfo) _logger.Info(GetLogMessage("Compacting receipts block database"));
                    _receiptsBlockDb.Compact();
                }
            }
            finally
            {
                _progress.MarkEnd();
                _stopwatch?.Stop();
                timer.Stop();
            }

            if (!token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info(GetLogMessage("finished"));
            }
        }

        Block GetMissingBlock(long i, Keccak? blockHash)
        {
            if (_logger.IsDebug) _logger.Debug(GetLogMessage("warning", $"Block {i} not found. Logs will not be searchable for this block."));
            Block emptyBlock = EmptyBlock.Get();
            emptyBlock.Header.Number = i;
            emptyBlock.Header.Hash = blockHash;
            return emptyBlock;
        }

        static void ReturnMissingBlock(Block emptyBlock)
        {
            EmptyBlock.Return(emptyBlock);
        }

        IEnumerable<(long, Keccak)> GetBlockBodiesForMigration(CancellationToken token)
        {
            bool TryGetMainChainBlockHashFromLevel(long number, out Keccak? blockHash)
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

            for (long i = _toBlock - 1; i > 0; i--)
            {
                if (token.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info(GetLogMessage("cancelled"));
                    yield break;
                }

                if (TryGetMainChainBlockHashFromLevel(i, out Keccak? blockHash))
                {
                    yield return (i, blockHash!);
                }

                if (_receiptStorage.MigratedBlockNumber > i)
                {
                    _receiptStorage.MigratedBlockNumber = i;
                }
            }
        }

        private void MigrateBlock(Block block)
        {
            TxReceipt?[] receipts = _receiptStorage.Get(block);
            TxReceipt[] notNullReceipts = receipts.Length == 0
                ? Array.Empty<TxReceipt>()
                : receipts.Where(r => r is not null).Cast<TxReceipt>().ToArray();

            if (notNullReceipts.Length == 0) return;

            _receiptStorage.Insert(block, notNullReceipts);

            // I guess some old schema need this
            {
                using IBatch batch = _receiptsDb.StartBatch();
                for (int i = 0; i < notNullReceipts.Length; i++)
                {
                    batch[notNullReceipts[i].TxHash!.Bytes] = null;
                }
            }

            // Receipts are now prefixed with block number.
            _receiptsBlockDb.Delete(block.Hash!);

            // Remove old tx index
            bool txIndexExpired = _receiptConfig.TxLookupLimit != 0 && _blockTree.Head?.Number - block.Number > _receiptConfig.TxLookupLimit;
            bool neverIndexTx = _receiptConfig.TxLookupLimit == -1;
            if (neverIndexTx || txIndexExpired)
            {
                using IBatch batch = _txIndexDb.StartBatch();
                foreach (TxReceipt? receipt in notNullReceipts)
                {
                    batch[receipt.TxHash!.Bytes] = null;
                }
            }

            if (notNullReceipts.Length != receipts.Length)
            {
                if (_logger.IsWarn)
                    _logger.Warn(GetLogMessage("warning",
                        $"Block {block.ToString(Block.Format.FullHashAndNumber)} is missing {receipts.Length - notNullReceipts.Length} of {receipts.Length} receipts!"));
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

        private bool IsMigrationNeeded(long blockNumber, Keccak blockHash, TxReceipt[] receipts)
        {
            if (!_receiptConfig.CompactReceiptStore && _recovery.NeedRecover(receipts))
            {
                return true;
            }

            byte[]? receiptData = _receiptsBlockDb.Get(blockHash.Bytes);
            if (receiptData == null)
            {
                receiptData = _receiptsBlockDb.Get(Bytes.Concat(blockNumber.ToBigEndianByteArray(), blockHash.Bytes));
            }

            if (receiptData == null)
            {
                return true;
            }

            bool isCompactEncoding = _storageDecoder.IsCompactEncoding(receiptData!);
            return _receiptConfig.CompactReceiptStore != isCompactEncoding;
        }

        private string GetLogMessage(string status, string? suffix = null)
        {
            string message = $"ReceiptsDb migration {status} | {_stopwatch?.Elapsed:d\\:hh\\:mm\\:ss} | {_progress.CurrentValue.ToString().PadLeft(_toBlock.ToString().Length)} / {_toBlock} blocks migrated. | current {_progress.CurrentPerSecond:F2} Blk/s | total {_progress.TotalPerSecond:F2} Blk/s. {suffix}";
            _progress.SetMeasuringPoint();
            return message;
        }

        private class EmptyBlockObjectPolicy : IPooledObjectPolicy<Block>
        {
            public Block Create()
            {
                return new Block(new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.Zero, 0L, 0L, 0UL, Array.Empty<byte>()));
            }

            public bool Return(Block obj)
            {
                return true;
            }
        }
    }
}
