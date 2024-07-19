using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Microsoft.VisualBasic;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
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

        private readonly string rootDirectory = @"C:\Users\merto\Programming\Nethermind\customIndexLukasczFINAL7";
        private readonly ConcurrentDictionary<Hash256AsKey, HashSet<int>> topicDictionary = new ConcurrentDictionary<Hash256AsKey, HashSet<int>>();
        private readonly ConcurrentDictionary<AddressAsKey, HashSet<int>> addressDictionary = new ConcurrentDictionary<AddressAsKey, HashSet<int>>();
        private readonly ConcurrentDictionary<string, object> fileLocks = new ConcurrentDictionary<string, object>();
        private readonly object batchLock = new object();
        private int blocksProccessed = 0;
        private long totalBlocks;
        private const int BatchSize = 100;

        //take some number of blocks, make the internal collections as *lighweight as possible) <-- HOW???
        //Drop concurrentDict
        //loading of receipts is parallel!!!

        public ExtractKeyValueMigration(IApiWithNetwork api) : this(
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

        public ExtractKeyValueMigration(
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

            _logger.Info("Initializing directories for migration.");
            InitializeDirectories();
            _logger.Info("Finished initializing directories for migration.");
        }

        private void InitializeDirectories()
        {
            // Create root directory if it doesn't exist
            Directory.CreateDirectory(rootDirectory);

            // Create subdirectories for the first three characters 0-9, a-z
            foreach (char c1 in "0123456789abcdefghijklmnopqrstuvwxyz")
            {
                string subfolder1 = Path.Combine(rootDirectory, c1.ToString());
                Directory.CreateDirectory(subfolder1);

                foreach (char c2 in "0123456789abcdefghijklmnopqrstuvwxyz")
                {
                    string subfolder2 = Path.Combine(subfolder1, c2.ToString());
                    Directory.CreateDirectory(subfolder2);

                    foreach (char c3 in "0123456789abcdefghijklmnopqrstuvwxyz")
                    {
                        string subfolder3 = Path.Combine(subfolder2, c3.ToString());
                        Directory.CreateDirectory(subfolder3);
                        foreach (char c4 in "0123456789abcdefghijklmnopqrstuvwxyz")
                        {
                            string subfolder4 = Path.Combine(subfolder2, c4.ToString());
                            Directory.CreateDirectory(subfolder3);
                        }
                    }
                }
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
                if (_logger.IsInfo) _logger.Info($"KeyValueMigration in progress. TotalBlocks: {totalBlocks}. Synced: {blocksProccessed}. Blocks left: {totalBlocks - blocksProccessed}");
            };

            Dictionary<Hash256AsKey, WriterInfo> topicWriters = new();
            Dictionary<AddressAsKey, WriterInfo> addressWriters = new();

            try
            {
                int parallelism = _receiptConfig.ReceiptsMigrationDegreeOfParallelism;
                if (parallelism == 0)
                {
                    parallelism = Environment.ProcessorCount;
                }

                Span<byte> buffer = stackalloc byte[sizeof(int)];

                foreach ((long, TxReceipt[]) block in GetBlockBodiesForMigration(token)
                             .Select(i => _blockTree.FindBlock(i.Item2, BlockTreeLookupOptions.None) ?? GetMissingBlock(i.Item1, i.Item2))
                             .Select(b => (b.Number, _receiptStorage.Get(b, false))).AsParallel().AsOrdered())
                {
                    int blockNumber = (int)block.Item1;
                    BinaryPrimitives.WriteInt32LittleEndian(buffer, blockNumber);
                    foreach (TxReceipt? receipt in block.Item2)
                    {
                        if (receipt is { Logs: not null })
                        {
                            foreach (LogEntry log in receipt.Logs)
                            {
                                AddressAsKey key = log.LoggersAddress;

                                ref WriterInfo? writer = ref CollectionsMarshal.GetValueRefOrAddDefault(addressWriters, key, out bool exists);

                                if (!exists || writer is null)
                                {
                                    var fileStream = new FileStream(GetPath(key.Value.Bytes), FileMode.Append, FileAccess.Write, FileShare.Read);
                                    writer = new WriterInfo(fileStream);
                                }

                                if (writer.BlockNumber < blockNumber)
                                {
                                    writer.Writer.Write(buffer);
                                    writer.BlockNumber = blockNumber;
                                }

                                foreach (Hash256AsKey topic in log.Topics)
                                {
                                    ref WriterInfo? topicWriter = ref CollectionsMarshal.GetValueRefOrAddDefault(topicWriters, topic, out bool topicExists);
                                    if (!topicExists || topicWriter is null)
                                    {
                                        var fileStream = new FileStream(GetPath(topic.Value.Bytes), FileMode.Append, FileAccess.Write, FileShare.Read);
                                        topicWriter = new WriterInfo(fileStream);
                                    }

                                    if (topicWriter.BlockNumber < blockNumber)
                                    {
                                        topicWriter.Writer.Write(buffer);
                                        topicWriter.BlockNumber = blockNumber;
                                    }
                                }
                            }

                            if (topicDictionary.Count + addressDictionary.Count > 10_000)
                            {
                                CloseFiles(topicWriters, addressWriters);
                            }
                        }
                    }
                    blocksProccessed++;
                }
            }
            finally
            {
                CloseFiles(topicWriters, addressWriters);
                _progress.MarkEnd();
                _stopwatch?.Stop();
                timer.Stop();
            }

            if (!token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info("KeyValueMigration finished");
            }
        }

        private void CloseFiles(
            Dictionary<Hash256AsKey, WriterInfo> topicWriters,
            Dictionary<AddressAsKey, WriterInfo> addressWriters)
        {
            _logger.Info("Disposing & Closing Files");
            Parallel.ForEach(topicWriters.Values, info => info.Writer.Dispose());
            Parallel.ForEach(addressWriters.Values, info => info.Writer.Dispose());
            topicWriters.Clear();
            addressWriters.Clear();
        }

        private class WriterInfo(Stream writer)
        {
            public Stream Writer { get; } = writer;
            public int BlockNumber { get; set; }
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
                        var key = log.LoggersAddress;
                        AddToBatch(key, (int)block.Number);

                        foreach (Hash256 topic in log.Topics)
                        {
                            AddToBatch(topic, (int)block.Number);
                        }
                    }
                }
            }
        }

        private void AddToBatch(AddressAsKey key, int blockNumber)
        {
            addressDictionary.AddOrUpdate(key, new HashSet<int> { blockNumber }, (k, v) =>
            {
                v.Add(blockNumber);
                return v;
            });

            lock (batchLock)
            {
                if (addressDictionary.Count >= BatchSize)
                {
                    addressDictionary.Clear();
                }
            }
        }

        private void AddToBatch(Hash256AsKey key, int blockNumber)
        {
            topicDictionary.AddOrUpdate(key, new HashSet<int> { blockNumber }, (k, v) =>
            {
                v.Add(blockNumber);
                return v;
            });

            lock (batchLock)
            {
                if (topicDictionary.Count >= BatchSize)
                {
                    topicDictionary.Clear();
                }
            }
        }

        // private void WriteBatchToFile()
        // {
        //     foreach (var kvp in addressDictionary)
        //     {
        //         var blockNumbers = kvp.Value.ToImmutableSortedSet();
        //
        //         var filePath = GetPath(kvp);
        //
        //         var fileLock = fileLocks.GetOrAdd(filePath, new object());
        //
        //         lock (fileLock)
        //         {
        //             using var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        //             using var writer = new BinaryWriter(fileStream);
        //             foreach (var blockNumber in blockNumbers)
        //             {
        //                 writer.Write((int)blockNumber);
        //             }
        //         }
        //     }
        // }

        private string GetPath(Span<byte> key)
        {
            var keyAsString = key.ToHexString(false, true, false);

            // Ensure the keyAsString has at least 4 characters by padding with '0' if necessary
            if (keyAsString.Length < 4)
            {
                keyAsString = keyAsString.PadLeft(4, '0');
            }

            // Extract the first three characters
            string subfolder1 = keyAsString[0].ToString();
            string subfolder2 = keyAsString[1].ToString();
            string subfolder3 = keyAsString[2].ToString();
            string subfolder4 = keyAsString[3].ToString();

            // Combine the root directory with the subfolders and the key string to form the full path
            return Path.Combine(rootDirectory, subfolder1, subfolder2, subfolder3, key.ToHexString(false,false,false));
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
