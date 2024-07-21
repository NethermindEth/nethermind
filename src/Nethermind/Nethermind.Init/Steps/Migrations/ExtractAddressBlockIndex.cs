using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Crypto;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using Timer = System.Timers.Timer;

namespace Nethermind.Init.Steps.Migrations
{
    public class ExtractAddressBlockIndex : IDatabaseMigration
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

        private readonly string rootDirectory = @"C:\Users\merto\Programming\customIndex5";
        private readonly ConcurrentDictionary<string, ConcurrentHashSet<int>> keyValuePairs = new ConcurrentDictionary<string, ConcurrentHashSet<int>>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
        private const int BlockBatchSize = 30_000;
        private int blocksProcessedSinceLastFlush = 0;
        private long totalBlocks;
        private object flushLock = new object();

        public ExtractAddressBlockIndex(IApiWithNetwork api) : this(
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

        public ExtractAddressBlockIndex(
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

            InitializeDirectories();
        }

        private void InitializeDirectories()
        {
            // Create root directory if it doesn't exist
            Directory.CreateDirectory(rootDirectory);

            // Create subdirectories for 0-9, a-z, A-Z
            foreach (char c in "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
            {
                Directory.CreateDirectory(Path.Combine(rootDirectory, c.ToString()));
            }
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
                    await Wait.ForEventCondition<SyncModeChangedEventArgs>(
                        cancellationToken,
                        (e) => _syncModeSelector.Changed += e,
                        (e) => _syncModeSelector.Changed -= e,
                        (arg) => CanMigrate(arg.Current));
                }

                RunIfNeeded(cancellationToken);
            }
        }

        private static bool CanMigrate(SyncMode syncMode) => syncMode.NotSyncing();

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
            long synced = 1;
            _progress.Reset(synced);

            if (_logger.IsInfo) _logger.Info("KeyValueMigration started");

            using Timer timer = new(10000);
            timer.Enabled = true;
            timer.Elapsed += (_, _) =>
            {
                if (_logger.IsInfo) _logger.Info($"KeyValueMigration in progress. TotalBlocks: {totalBlocks}. Synced: {synced}. Blocks left: {totalBlocks - synced}");
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
                    (long blockNum, Hash256 blockHash) = item;
                    Block? block = _blockTree.FindBlock(blockHash!, BlockTreeLookupOptions.None);
                    bool usingEmptyBlock = block is null;
                    if (usingEmptyBlock)
                    {
                        block = GetMissingBlock(blockNum, blockHash);
                    }

                    _progress.Update(Interlocked.Increment(ref synced));
                    ExtractKeyValuePairs(block!);

                    if (usingEmptyBlock)
                    {
                        ReturnMissingBlock(block!);
                    }

                    if (Interlocked.Increment(ref blocksProcessedSinceLastFlush) >= BlockBatchSize)
                    {
                        lock (flushLock)
                        {
                            FlushToDisk();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Error appeared in {nameof(RunMigration)}:", ex);
            }
            finally
            {
                FlushToDisk();

                // Begin encoding files
                _logger.Info("Beginning file encoding...");

                Timer encodingTimer = new Timer(60000); // Log progress every 60 seconds
                encodingTimer.Elapsed += (sender, e) => _logger.Info("File encoding in progress...");
                encodingTimer.Start();

                EncodeFiles();

                encodingTimer.Stop();
                encodingTimer.Dispose();

                _progress.MarkEnd();
                _stopwatch?.Stop();
                timer.Stop();
            }

            if (!token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info("KeyValueMigration finished");
            }
        }

        private void ExtractKeyValuePairs(Block block)
        {
            TxReceipt?[] receipts = _receiptStorage.Get(block);
            foreach (TxReceipt? receipt in receipts)
            {
                if (receipt != null && receipt.Logs != null)
                {
                    foreach (LogEntry log in receipt.Logs)
                    {
                        string key = log.LoggersAddress.ToString();
                        AddToBatch(key, (int)block.Number);

                        foreach (Hash256 topic in log.Topics)
                        {
                            string topicKey = topic.ToString();
                            AddToBatch(topicKey, (int)block.Number);
                        }
                    }
                }
            }
        }

        private void AddToBatch(string key, int blockNumber)
        {
            keyValuePairs.AddOrUpdate(key, new ConcurrentHashSet<int> { blockNumber }, (k, v) =>
            {
                v.Add(blockNumber);
                return v;
            });
        }

        private void FlushToDisk()
        {
            if (blocksProcessedSinceLastFlush == 0)
            {
                return;
            }

            try
            {
                var tasks = new List<Task>();
                foreach (var kvp in keyValuePairs)
                {
                    ReadOnlySpan<char> keyAsSpan = kvp.Key.AsSpan(2).TrimStart('0');
                    keyAsSpan = keyAsSpan.IsEmpty ? "0" : keyAsSpan;
                    var blockNumbers = kvp.Value.ToImmutableSortedSet();

                    string subfolder = keyAsSpan[0].ToString(); // Get the first character after "0x"
                    string subfolderPath = Path.Combine(rootDirectory, subfolder);
                    string filePath = Path.Combine(subfolderPath, kvp.Key);

                    var task = Task.Run(() => WriteBlockNumsToFileAsync(blockNumbers, filePath));
                    tasks.Add(task);
                }

                Task.WaitAll(tasks.ToArray());

                keyValuePairs.Clear();
                blocksProcessedSinceLastFlush = 0;
            }
            catch (Exception ex)
            {
                _logger.Error("Something went wrong when flushing index to disk:", ex);
            }
        }

        private void WriteBlockNumsToFileAsync(ImmutableSortedSet<int> blockNumbers, string filePath)
        {
            try
            {
                byte[] data = new byte[4];
                using (var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true))
                {
                    using var writer = new BinaryWriter(fileStream);
                    foreach (var blockNumber in blockNumbers)
                    {
                        writer.Write(blockNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Writing to File failed", ex);
                throw;
            }
        }

        private void EncodeFiles()
        {
            try
            {
                var files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    var data = File.ReadAllBytes(file);

                    int[] intData = new int[data.Length / 4];
                    Buffer.BlockCopy(data, 0, intData, 0, data.Length);

                    byte[] compressedData = new byte[data.Length];
                    int compressedSize = TurboPFor.p4nd1enc256v32(intData, intData.Length, compressedData);

                    File.WriteAllBytes(file, compressedData.Take(compressedSize).ToArray());
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error occurred during file encoding:", ex);
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


            for (long i = _blockTree.BestKnownNumber - 1; i > 0; i--)
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
