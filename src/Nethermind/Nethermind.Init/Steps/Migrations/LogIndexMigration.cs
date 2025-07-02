using System;
using System.Diagnostics;
using System.IO;
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
        private readonly Stopwatch _stopwatch = new();

        private readonly ProgressLogger _progress;
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
        private const int BatchSize = 250;
        private const int QueueSize = 25_000;
        private const int ReportSize = 50_000;
        private readonly Channel<BlockReceipts[]> _blocksChannel;

        private long _totalBlocks;

        private readonly LogIndexUpdateStats _totalStats = new();
        private LogIndexUpdateStats _lastStats = new();

        public LogIndexMigration(IApiWithNetwork api) : this(
            api.LogIndexStorage!,
            api.ReceiptStorage!,
            api.BlockTree!,
            api.SyncModeSelector!,
            api.Config<IReceiptConfig>(),
            api.DbProvider?.ReceiptsDb!,
            new ReceiptsRecovery(api.EthereumEcdsa, api.SpecProvider),
            api.LogManager,
            api.Config<IInitConfig>())
        { }

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
            _blocksChannel = Channel.CreateBounded<BlockReceipts[]>(new BoundedChannelOptions(QueueSize / BatchSize)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _progress = new("Log-index Migration", logManager);
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
            if (_totalBlocks == 0)
                return;

            if (_logger.IsInfo)
            {
                (LogIndexUpdateStats last, LogIndexUpdateStats total) = (_lastStats, _totalStats);
                _lastStats = new();

                if (total.MaxBlockNumber < 0)
                    total.MaxBlockNumber = _logIndexStorage.GetMaxBlockNumber() ?? -1;

                _logger.Info($"LogIndexMigration" +

                    $"\n\t\tBlocks: {total.MaxBlockNumber:N0} / {_totalBlocks:N0} ( {(decimal)total.MinBlockNumber! / _totalBlocks * 100:F2} % ) ( +{last.BlocksAdded:N0} ) ( {_blocksChannel.Reader.Count} * {BatchSize} in queue )" +
                    $"\n\t\tTxs: {total.TxAdded:N0} ( +{last.TxAdded:N0} )" +
                    $"\n\t\tLogs: {total.LogsAdded:N0} ( +{last.LogsAdded:N0} )" +
                    $"\n\t\tTopics: {total.TopicsAdded:N0} ( +{last.TopicsAdded:N0} )" +
                    "\n" +
                    $"\n\t\tWaiting batch: {last.WaitingBatch} ( {total.WaitingBatch} on average )" +
                    $"\n\t\tKeys per batch: {last.KeysCount:N0} ( {total.KeysCount:N0} on average )" +
                    $"\n\t\tBuilding dictionary: {last.BuildingDictionary} ( {total.BuildingDictionary} on average )" +
                    $"\n\t\tProcessing: {last.Processing} ( {total.Processing} on average )" +
                    $"\n\t\tMerge call: {last.CallingMerge} ( {total.CallingMerge} on average )" +
                    $"\n\t\tIn-memory merging: {last.InMemoryMerging} ( {total.InMemoryMerging} in total )" +
                    "\n" +
                    $"\n\t\tCompacting DBs: {last.Compacting.Total} ( {total.Compacting.Total} on average )" +
                    $"\n\t\tPost-merge processing: {last.PostMergeProcessing.Execution} ( {total.PostMergeProcessing.Execution} in total )" +
                    $"\n\t\t\tDB getting: {last.PostMergeProcessing.GettingValue} ( {total.PostMergeProcessing.GettingValue} in total )" +
                    $"\n\t\t\tCompressing: {last.PostMergeProcessing.CompressingValue} ( {total.PostMergeProcessing.CompressingValue} in total )" +
                    $"\n\t\t\tPutting: {last.PostMergeProcessing.PuttingValues} ( {total.PostMergeProcessing.PuttingValues} in total )" +
                    $"\n\t\t\tCompressed keys: {last.PostMergeProcessing.CompressedAddressKeys:N0} address, {last.PostMergeProcessing.CompressedTopicKeys:N0} topic ( {total.PostMergeProcessing.CompressedAddressKeys:N0} address, {total.PostMergeProcessing.CompressedTopicKeys:N0} topic in total )" +
                    $"\n\t\tDB size: {GetFolderSize(Path.Combine(_initConfig.BaseDbPath, DbNames.LogIndex))}"
                );
            }
        }

        private static readonly string[] Suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

        public static string GetFolderSize(string path)
        {
            var info = new DirectoryInfo(path);

            double size = info.Exists ? info.GetFiles().Sum(f => f.Length) : 0;

            int index = 0;
            while (size >= 1024 && index < Suffixes.Length - 1)
            {
                size /= 1024;
                index++;
            }

            return $"{size:0.##} {Suffixes[index]}";
        }

        private async Task RunMigration(CancellationToken token)
        {
            _stopwatch.Start();

            if (_logger.IsInfo) _logger.Info("LogIndexMigration started");

            using Timer timer = new(10_000);
            timer.Enabled = true;
            timer.Elapsed += LogStats;

            try
            {
                // await _logIndexStorage.CheckMigratedData();

                var iterateTask = Task.Run(() => QueueBlocks(_blocksChannel.Writer, token), token);
                var migrateTask = Task.Run(() => MigrateBlocks(_blocksChannel.Reader, token), token);
                await Task.WhenAll(iterateTask, migrateTask);

                // await Task.CompletedTask;
                // var stats = _logIndexStorage.CompressAndCompact(LogIndexStorage.MaxUncompressedLength / 4);
                // _logger.Info($"LogIndexMigration" +
                //     $"\n\t\tQueueing for compression: {stats.QueueingAddressCompression} address, {stats.QueueingTopicCompression} topic" +
                //     $"\n\t\tCompacting DBs: {stats.CompactingDbs}" +
                //     $"\n\t\tFlushing DBs: {stats.FlushingDbs}" +
                //     $"\n\t\tPost-merge processing: {stats.PostMergeProcessing}" +
                //     $"\n\t\tCompressed keys: {stats.CompressedAddressKeys:N0} address, {stats.CompressedTopicKeys:N0} topic"
                // );
            }
            finally
            {
                //await _logIndexStorage.DisposeAsync();
                _progress.MarkEnd();
                _stopwatch.Stop();
                timer.Stop();
            }

            if (!token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info($"LogIndexMigration finished in {_stopwatch.Elapsed}");
            }
        }

        private async Task QueueBlocks(ChannelWriter<BlockReceipts[]> writer, CancellationToken token)
        {
            try
            {
                var startFrom = 0;
                // var startFrom = 750_000; // Holesky: Just before slowdown
                // var startFrom = 750_000 + 18_000 + 33_000; // Holesky: Where slowdown starts
                // var startFrom = 2_000_000; // Holesky: Average blocks
                // var startFrom = 2_000_000 + 180_000; // Holesky: Very log-dense blocks
                // var startFrom =  4_750_000; // 0x487AB0 // Ethereum: Where slowdown starts
                // var startFrom = 11_000_000; // Ethereum: ~50%
                // var startFrom = 18_000_000; // Ethereum: ~4M to finish
                // var startFrom = 20_000_000; // Ethereum: ~2M to finish

                // TODO: move to chain configuration
                startFrom = Math.Max(startFrom, 52_029); // Ethereum: fist block with logs

                startFrom = Math.Max(startFrom, (_logIndexStorage.GetMaxBlockNumber() ?? -1) + 1);

                _totalBlocks = _blockTree.BestKnownNumber;
                //_totalBlocks = startFrom + 1_000_000;
                for (long i = startFrom; i < _totalBlocks; i += BatchSize)
                {
                    BlockReceipts[] batch = GetBlocks(i, Math.Min(i + BatchSize, _blockTree.BestKnownNumber), token);
                    await writer.WriteAsync(batch, token);
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

        private async Task MigrateBlocks(ChannelReader<BlockReceipts[]> reader, CancellationToken token)
        {
            try
            {
                var (migrated, prevMigrated) = (0, 0);
                var watch = Stopwatch.StartNew();
                var prevElapsed = TimeSpan.Zero;

                var timestamp = Stopwatch.GetTimestamp();
                await foreach (var batch in reader.ReadAllAsync(token))
                {
                    var readElapsed = Stopwatch.GetElapsedTime(timestamp);

                    if (token.IsCancellationRequested)
                        return;

                    LogIndexUpdateStats runStats = await _logIndexStorage.SetReceiptsAsync(batch, isBackwardSync: false);
                    runStats.WaitingBatch.Include(readElapsed);
                    migrated += batch.Length;

                    if (ReportSize > 0 && (migrated - prevMigrated) >= ReportSize)
                    {
                        TimeSpan elapsed = watch.Elapsed;
                        _logger.Info($"LogIndexMigration: Migrated {migrated} blocks in {elapsed} ( +{migrated - prevMigrated} in {elapsed - prevElapsed} )");

                        prevElapsed = elapsed;
                        prevMigrated = migrated;
                    }

                    _lastStats.Combine(runStats);
                    _totalStats.Combine(runStats);

                    timestamp = Stopwatch.GetTimestamp();
                }
            }
            catch (OperationCanceledException canceledEx) when (canceledEx.CancellationToken == token)
            {
                // Cancelled
            }
        }

        private BlockReceipts[] GetBlocks(long from, long to, CancellationToken token)
        {
            var batch = new BlockReceipts[to - from];
            Parallel.For(from, to, new()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = _receiptConfig.LogIndexIOParallelism
            }, i =>
            {
                Block block = _blockTree.FindBlock(i) ?? GetMissingBlock(i);
                TxReceipt[] receipts = _receiptStorage.Get(block, false);
                batch[(int)(i - from)] = new((int)block.Number, receipts);
            });

            return batch;
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
