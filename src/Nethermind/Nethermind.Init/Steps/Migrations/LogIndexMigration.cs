using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Timers;
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
        private const int BatchSize = 100;
        private readonly Channel<(int blockNumber, TxReceipt[] receipts)[]> _blocksChannel;

        private long _totalBlocks;

        private readonly SetReceiptsStats _totalStats = new();
        private SetReceiptsStats _lastStats = new();

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
            _blocksChannel = Channel.CreateBounded<(int blockNumber, TxReceipt[] receipts)[]>(new BoundedChannelOptions(1000 / BatchSize)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
            });
        }

        public async Task<bool> Run(long blockNumber)
        {
            await (_cancellationTokenSource?.CancelAsync() ?? Task.CompletedTask);
            await (_migrationTask ?? Task.CompletedTask);

            _cancellationTokenSource = new();
            _receiptStorage.MigratedBlockNumber =
                Math.Min(Math.Max(_receiptStorage.MigratedBlockNumber, blockNumber), (_blockTree.Head?.Number ?? 0) + 1);
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

                await RunIfNeeded(cancellationToken);
            }
        }

        // TODO add conditions
        private static bool CanMigrate(SyncMode syncMode) => true;

        private async Task RunIfNeeded(CancellationToken cancellationToken)
        {
            _stopwatch = Stopwatch.StartNew();
            try
            {
                await RunMigration(cancellationToken);
            }
            catch (Exception e)
            {
                _stopwatch.Stop();
                _logger.Error($"Migration failed: {e}", e);
            }
        }

        private void LogStats(object? sender, ElapsedEventArgs e)
        {
            if (_logger.IsInfo)
            {
                (SetReceiptsStats last, SetReceiptsStats total) = (_lastStats, _totalStats);
                _lastStats = new();

                _logger.Info($"LogIndexMigration" +
                    $"\n\t\tBlocks: {total.BlocksAdded:N0} / {_totalBlocks:N0} ( {(decimal)total.BlocksAdded / _totalBlocks * 100:F2} % ) ( +{last.BlocksAdded:N0} ) ( {_blocksChannel.Reader.Count} * {BatchSize} in queue )" +
                    $"\n\t\tTxs: {total.TxAdded:N0} ( +{last.TxAdded} )" +
                    $"\n\t\tLogs: {total.LogsAdded:N0} ( +{last.LogsAdded:N0} )" +
                    $"\n\t\tTopics: {total.TopicsAdded:N0} ( +{last.TopicsAdded:N0} )" +
                    $"\n\t\tSeekForPrev: {last.SeekForPrevHit} / {last.SeekForPrevMiss}" +
                    $"\n\t\tPages: {last.PagesAllocated} allocated, {last.PagesTaken} taken, {last.PagesReturned} returned");
            }
        }

        private async Task RunMigration(CancellationToken token)
        {
            if (_logger.IsInfo) _logger.Info("LogIndexMigration started");

            using Timer timer = new(10_000);
            timer.Enabled = true;
            timer.Elapsed += LogStats;

            try
            {
                // TODO use separate config option?
                int parallelism = _receiptConfig.ReceiptsMigrationDegreeOfParallelism;
                if (parallelism == 0) parallelism = Environment.ProcessorCount;

                Task iterateTask = QueueBlocks(_blocksChannel.Writer, BatchSize, token);
                Task migrateTask = MigrateBlocks(_blocksChannel.Reader, 1, token);
                await Task.WhenAll(iterateTask, migrateTask);
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

        private async Task QueueBlocks(ChannelWriter<(int blockNumber, TxReceipt[] receipts)[]> writer, int batchSize, CancellationToken token)
        {
            List<(int blockNumber, TxReceipt[] receipts)> batch = new(batchSize);

            try
            {
                //foreach (Block block in GetBlocksForMigration(token, startFrom: 2_000_000))
                //foreach (Block block in GetBlocksForMigration(token, startFrom: 750_000)) // Where slowdown starts
                foreach (Block block in GetBlocksForMigration(token, startFrom: 0))
                {
                    TxReceipt[] receipts = _receiptStorage.Get(block, false);

                    batch.Add(((int)block.Number, receipts));
                    if (batch.Count < batchSize) continue;

                    await writer.WriteAsync(batch.ToArray(), token);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException canceledEx) when (canceledEx.CancellationToken == token)
            {
                // Cancelled
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Failed to enumerate blocks for migration", ex);
            }
            finally
            {
                writer.Complete();
            }
        }

        private async Task MigrateBlocks(ChannelReader<(int blockNumber, TxReceipt[] receipts)[]> reader, int parallelism, CancellationToken token)
        {
            try
            {
                if (parallelism > 1)
                {
                    await Parallel.ForEachAsync(
                        reader.ReadAllAsync(token),
                        new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = parallelism },
                        (batch, _) =>
                        {
                            SetReceiptsStats runStats = _logIndexStorage.SetReceipts(batch, isBackwardSync: false, token);
                            _lastStats.Add(runStats);
                            _totalStats.Add(runStats);

                            return ValueTask.CompletedTask;
                        }
                    );
                }
                else
                {
                    foreach ((int blockNumber, TxReceipt[] receipts)[] batch in reader.ReadAllAsync(token).ToEnumerable())
                    {
                        if (token.IsCancellationRequested)
                            return;

                        SetReceiptsStats runStats = _logIndexStorage.SetReceipts(batch, isBackwardSync: false, token);
                        _lastStats.Add(runStats);
                        _totalStats.Add(runStats);
                    }
                }
            }
            catch (OperationCanceledException canceledEx) when (canceledEx.CancellationToken == token)
            {
                // Cancelled
            }
        }

        private IEnumerable<Block> GetBlocksForMigration(CancellationToken token, int startFrom)
        {
            _totalBlocks = _blockTree.BestKnownNumber;

            // TODO: start from 0!
            for (long i = startFrom; i < _totalBlocks - 1; i++)
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
