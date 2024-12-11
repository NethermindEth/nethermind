using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Timer = System.Timers.Timer;

namespace Nethermind.Init.Steps.Migrations
{
    public class LogIndexMigration : IDatabaseMigration
    {
        private static readonly ObjectPool<Block> EmptyBlock = new DefaultObjectPool<Block>(new EmptyBlockObjectPolicy());

        private readonly ILogger _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        internal Task? _migrationTask;
        private Stopwatch? _stopwatch;

        private readonly MeasuredProgress _progress = new();
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockTree _blockTree;
        private readonly ISyncModeSelector _syncModeSelector;

        private readonly IReceiptConfig _receiptConfig;
        private readonly IColumnsDb<ReceiptsColumns> _receiptsDb;
        private readonly IDb _txIndexDb;
        private readonly IDb _receiptsBlockDb;
        private readonly IReceiptsRecovery _recovery;
        private readonly ILogIndexStorage _logIndexStorage;
        private readonly IInitConfig _initConfig;
        private long txProcessed;
        private int blocksProcessed;
        private long totalBlocks;

        public LogIndexMigration(IApiWithNetwork api) : this(
            api.LogIndexStorage!,
            api.ReceiptStorage!,
            api.BlockTree!,
            api.SyncModeSelector!,
            api.Config<IReceiptConfig>(),
            api.DbProvider?.ReceiptsDb!,
            new ReceiptsRecovery(api.EthereumEcdsa, api.SpecProvider),
            api.LogManager,
            api.Config<IInitConfig>()) { }

        public LogIndexMigration(
            ILogIndexStorage logIndexStorage,
            IReceiptStorage receiptStorage,
            IBlockTree blockTree,
            ISyncModeSelector syncModeSelector,
            IReceiptConfig receiptConfig,
            IColumnsDb<ReceiptsColumns> receiptsDb,
            IReceiptsRecovery recovery,
            ILogManager logManager,
            IInitConfig initConfig
        )
        {
            _logIndexStorage = logIndexStorage ?? throw new StepDependencyException(nameof(logIndexStorage));
            _receiptStorage = receiptStorage ?? throw new StepDependencyException(nameof(receiptStorage));
            _blockTree = blockTree ?? throw new StepDependencyException(nameof(blockTree));
            _syncModeSelector = syncModeSelector ?? throw new StepDependencyException(nameof(syncModeSelector));
            _receiptConfig = receiptConfig ?? throw new StepDependencyException("receiptConfig");
            _receiptsDb = receiptsDb;
            _receiptsBlockDb = _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);
            _txIndexDb = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
            _recovery = recovery;
            _initConfig = initConfig;
            _logger = logManager.GetClassLogger();
        }

        public async Task<bool> Run(long blockNumber)
        {
            await (_cancellationTokenSource?.CancelAsync() ?? Task.CompletedTask);
            await (_migrationTask ?? Task.CompletedTask);

            _cancellationTokenSource = new();
            _receiptStorage.MigratedBlockNumber = Math.Min(Math.Max(_receiptStorage.MigratedBlockNumber, blockNumber), (_blockTree.Head?.Number ?? 0) + 1);
            _migrationTask = DoRun(_cancellationTokenSource.Token);

            return _receiptConfig is { StoreReceipts: true, ReceiptsMigration: true };
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

        // TODO add conditions
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
            if (_logger.IsInfo) _logger.Info("LogIndexMigration started");

            using Timer timer = new(10_000);
            timer.Enabled = true;
            timer.Elapsed += (_, _) =>
            {
                if (_logger.IsInfo)
                {
                    var seekForPrevMes = _logIndexStorage.SeekForPrevMeasurement;
                    _logIndexStorage.SeekForPrevMeasurement = new();
                    _logger.Info($"LogIndexMigration {blocksProcessed:N0} / {totalBlocks:N0} ( {(decimal)blocksProcessed / totalBlocks * 100 :F2} % ) [tx: {txProcessed:N0}] [SeekForPrev: {seekForPrevMes}]");
                }
            };

            try
            {
                // TODO use separate config option?
                int parallelism = _receiptConfig.ReceiptsMigrationDegreeOfParallelism;
                if (parallelism == 0) parallelism = Environment.ProcessorCount;

                foreach (Block block in GetBlocksForMigration(token)
                             .AsParallel().AsOrdered().WithDegreeOfParallelism(parallelism)
                        )
                {
                    TxReceipt[] receipts = _receiptStorage.Get(block, false);
                    _logIndexStorage.SetReceipts((int)block.Number, receipts, isBackwardSync: false);
                    blocksProcessed++;
                    txProcessed += receipts.Length;
                }
            }
            finally
            {
                _logIndexStorage.Dispose();
                _progress.MarkEnd();
                _stopwatch!.Stop();
                timer.Stop();
            }

            if (!token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info($"LogIndexMigration finished in {_stopwatch!.Elapsed}");
            }
        }

        private IEnumerable<Block> GetBlocksForMigration(CancellationToken token)
        {
            totalBlocks = _blockTree.BestKnownNumber;

            // TODO: start from 0!
            for (long i = 2_000_000; i < totalBlocks - 1; i++)
            {
                if (token.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info("LogIndexMigration cancelled");
                    yield break;
                }

                if (_receiptStorage.MigratedBlockNumber > i)
                {
                    _receiptStorage.MigratedBlockNumber = i;
                }

                yield return _blockTree.FindBlock(i) ?? GetMissingBlock(i);
            }
        }

        Block GetMissingBlock(long i)
        {
            if (_logger.IsDebug) _logger.Debug($"Block {i} not found. Logs will not be searchable for this block.");
            Block emptyBlock = EmptyBlock.Get();
            emptyBlock.Header.Number = i;
            return emptyBlock;
        }

        // TODO: check if single static Block field can be used instead
        //  What's the point if block is never returned to the pool?
        private class EmptyBlockObjectPolicy : IPooledObjectPolicy<Block>
        {
            public Block Create()
            {
                return new(new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.Zero, 0L, 0L, 0UL, []));
            }

            public bool Return(Block obj)
            {
                return true;
            }
        }
    }
}
