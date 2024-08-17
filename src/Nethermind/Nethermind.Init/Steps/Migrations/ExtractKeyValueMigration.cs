using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using RocksDbSharp;
using Timer = System.Timers.Timer;

namespace Nethermind.Init.Steps.Migrations
{
    public class ExtractKeyValueMigration : IDatabaseMigration
    {
        private static readonly ObjectPool<Block> EmptyBlock = new DefaultObjectPool<Block>(new EmptyBlockObjectPolicy());

        private readonly ILogger _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        internal Task? _migrationTask;
        private Stopwatch? _stopwatch;

        private readonly MeasuredProgress _progress = new MeasuredProgress();
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
        private readonly LogIndexStorage _logIndexStorage;
        private int blocksProcessed = 0;
        private long totalBlocks;

        public ExtractKeyValueMigration(IApiWithNetwork api) : this(
            api.ReceiptStorage!,
            api.BlockTree!,
            api.SyncModeSelector!,
            api.ChainLevelInfoRepository!,
            api.Config<IReceiptConfig>(),
            api.DbProvider?.ReceiptsDb!,
            api.DbProvider?.LogIndexDb!,
            new ReceiptsRecovery(api.EthereumEcdsa, api.SpecProvider),
            api.LogManager
        )
        {
        }

        public ExtractKeyValueMigration(
            IReceiptStorage receiptStorage,
            IBlockTree blockTree,
            ISyncModeSelector syncModeSelector,
            IChainLevelInfoRepository chainLevelInfoRepository,
            IReceiptConfig receiptConfig,
            IColumnsDb<ReceiptsColumns> receiptsDb,
            IColumnsDb<LogIndexColumns> logIndexDb,
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

            // Initialize LogIndexStorage
            _logIndexStorage = new LogIndexStorage(
                columnsDb: logIndexDb,
                logger: _logger,
                tempFilePath: "temp_index.bin",
                finalFilePath: "finalized_index.bin"
            );

            _logger.Info("Initializing directories for migration.");
            _logger.Info("Finished initializing directories for migration.");
        }

        public async Task<bool> Run(long blockNumber)
        {
            _cancellationTokenSource?.Cancel();
            await (_migrationTask ?? Task.CompletedTask);
            _cancellationTokenSource = new CancellationTokenSource();
            _receiptStorage.MigratedBlockNumber = Math.Min(Math.Max(_receiptStorage.MigratedBlockNumber, blockNumber), (_blockTree.Head?.Number ?? 0) + 1);
            _migrationTask = DoRun(_cancellationTokenSource.Token);
            return _receiptConfig.StoreReceipts && _receiptConfig.ReceiptsMigration;
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            await DoRun(cancellationToken);
        }

        private async Task DoRun(CancellationToken cancellationToken)
        {
            if (_receiptConfig.StoreReceipts)
            {
                if (!CanMigrate(_syncModeSelector.Current))
                {
                    _logger.Info($"Waiting for {nameof(SyncModeChangedEventArgs)} to finish.");
                    await Wait.ForEventCondition<SyncModeChangedEventArgs>(
                        cancellationToken,
                        (e) => _syncModeSelector.Changed += e,
                        (e) => _syncModeSelector.Changed -= e,
                        (arg) => CanMigrate(arg.Current));
                }

                _logger.Info($"Finished waiting for {nameof(SyncModeChangedEventArgs)}");

                RunIfNeeded(cancellationToken);
            }
        }

        private static bool CanMigrate(SyncMode syncMode) => true;

        private void RunIfNeeded(CancellationToken cancellationToken)
        {
            _stopwatch = Stopwatch.StartNew();
            try
            {
                RunMigration(cancellationToken);
            }
            catch (Exception e)
            {
                _stopwatch.Stop();
                _logger.Error($"Migration failed: {e}", e);
            }
        }

        private void RunMigration(CancellationToken token)
        {
            if (_logger.IsInfo) _logger.Info("KeyValueMigration started");

            using Timer timer = new(10000);
            timer.Enabled = true;
            timer.Elapsed += (_, _) =>
            {
                if (_logger.IsInfo) _logger.Info($"KeyValueMigration in progress. TotalBlocks: {totalBlocks}. Synced: {blocksProcessed}. Blocks left: {totalBlocks - blocksProcessed}");
            };

            try
            {
                foreach ((long blockNumber, TxReceipt[] receipts) block in GetBlockBodiesForMigration(token)
                             .Select(i => _blockTree.FindBlock(i.Item2, BlockTreeLookupOptions.None) ?? GetMissingBlock(i.Item1, i.Item2))
                             .Select(b => (b.Number, _receiptStorage.Get(b, false))).AsParallel().AsOrdered())
                {
                    // Use LogIndexStorage to handle receipts
                    _logIndexStorage.SetReceipts((int)block.blockNumber, block.receipts, isBackwardSync: false);
                    blocksProcessed++;
                }
            }
            finally
            {
                _logIndexStorage.Dispose();
                _progress.MarkEnd();
                _stopwatch?.Stop();
                timer.Stop();
            }

            if (!token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info("KeyValueMigration finished");
            }
        }

        private IEnumerable<(long, Hash256)> GetBlockBodiesForMigration(CancellationToken token)
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

            totalBlocks = _blockTree.BestKnownNumber;

            for (long i = 0; i < _blockTree.BestKnownNumber - 1; i++)
            {
                if (token.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info("KeyValueMigration cancelled");
                    yield break;
                }

                if (TryGetMainChainBlockHashFromLevel(i, out Hash256? blockHash))
                {
                    yield return (i, blockHash!);
                }

                if (_receiptStorage.MigratedBlockNumber > i)
                {
                    _receiptStorage.MigratedBlockNumber = i;
                }
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
