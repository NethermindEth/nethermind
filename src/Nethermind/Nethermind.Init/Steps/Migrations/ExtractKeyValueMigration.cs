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
using Microsoft.Win32.SafeHandles;
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
        private readonly IDbWithIterator _logIndexDb;
        private readonly IReceiptsRecovery _recovery;

        static string finalFilePath = "finalizied_index.bin";
        private static SafeFileHandle finalizedFileHandle = File.OpenHandle(finalFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        static string tempFilePath = "temp_index.bin";
        private static SafeFileHandle tempFileHandle = File.OpenHandle(tempFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        private FileStream finalizedFileStream = new FileStream(finalizedFileHandle, FileAccess.ReadWrite);
        private FileStream tempFileStream = new FileStream(tempFileHandle, FileAccess.ReadWrite);
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
            IDbWithIterator logIndexDb,
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
            _logIndexDb = logIndexDb;
            _recovery = recovery;
            _logger = logManager.GetClassLogger();

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

        public enum FileType : byte
        {
            TEMP = 0x01,
            FINAL = 0x02,
        }
        private static byte[] AppendArrays(byte[] array1, byte[] array2)
        {
            byte[] result = new byte[array1.Length + array2.Length];
            Buffer.BlockCopy(array1, 0, result, 0, array1.Length);
            Buffer.BlockCopy(array2, 0, result, array1.Length, array2.Length);
            return result;
        }

        public static IEnumerable<(byte[] Key, byte[] Value)> GetKeyValuePairsWithPrefix(IDbWithIterator logIndexDb, byte[] prefix)
        {
            using Iterator iterator = logIndexDb.CreateIterator(true);
            iterator.Seek(prefix);

            while (iterator.Valid())
            {
                if (!iterator.Key().Take(prefix.Length).SequenceEqual(prefix))
                    break;

                yield return (iterator.Key(), iterator.Value());
                iterator.Next();
            }
        }
        private unsafe void RunMigration(CancellationToken token)
        {
            if (_logger.IsInfo) _logger.Info("KeyValueMigration started");

            using Timer timer = new(10000);
            timer.Enabled = true;
            timer.Elapsed += (_, _) =>
            {
                if (_logger.IsInfo) _logger.Info($"KeyValueMigration in progress. TotalBlocks: {totalBlocks}. Synced: {blocksProcessed}. Blocks left: {totalBlocks - blocksProcessed}");
            };

            Span<byte> metaDataKey = Encoding.UTF8.GetBytes("Metadata_free_slots");
            if (_logIndexDb.KeyExists(metaDataKey) == false)
            {
                _logIndexDb.PutSpan(metaDataKey, Array.Empty<byte>());
            }

            try
            {
                int parallelism = _receiptConfig.ReceiptsMigrationDegreeOfParallelism;
                if (parallelism == 0)
                {
                    parallelism = Environment.ProcessorCount;
                }

                int bufferSize = 4096; //use arrayPool
                Span<byte> buffer = stackalloc byte[bufferSize];
                Span<byte> bufferForBlockNum = stackalloc byte[4];
                byte[] bufferForEncodedInts = new byte[bufferSize]; //ArrayPool OR ArrayPoolList
                Span<byte> location = stackalloc byte[17];
                Span<byte> keyBytes = stackalloc byte[24];
                Span<int> decodedBlockNumbers = stackalloc int[bufferSize / 4];
                ArrayPoolList<byte> blockNumbers = new(bufferSize);


                foreach ((long, TxReceipt[]) block in GetBlockBodiesForMigration(token)
                             .Select(i => _blockTree.FindBlock(i.Item2, BlockTreeLookupOptions.None) ?? GetMissingBlock(i.Item1, i.Item2))
                             .Select(b => (b.Number, _receiptStorage.Get(b, false))).AsParallel().AsOrdered())
                {
                    //TODO: Move stuff to LogIndexStorage for reusability

                    int blockNumber = (int)block.Item1;
                    BinaryPrimitives.WriteInt32LittleEndian(bufferForBlockNum, blockNumber);
                    foreach (TxReceipt? receipt in block.Item2)
                    {
                        if (receipt is { Logs: not null })
                        {
                            foreach (LogEntry log in receipt.Logs)
                            {
                                AddressAsKey address = log.LoggersAddress;
                                address.Value.Bytes.CopyTo(keyBytes);
                                long position = 0;
                                int slotSize = 0;
                                int lastBlockNumber = blockNumber;
                                // check if theres any temp file location for the address
                                // if none then create a new index with the block number
                                // if there exists then add to the position with the given offset
                                var lastEntryForAddress = GetKeyValuePairsWithPrefix(_logIndexDb, keyBytes.ToArray()).LastOrDefault();

                                //TODO: use bit for flagging if Its final or not
                                if (lastEntryForAddress == default || lastEntryForAddress.Value[0] == (byte)FileType.FINAL)
                                {
                                    // no index found for the address
                                    Span<byte> freeSlots = _logIndexDb.GetSpan(metaDataKey);
                                    if (freeSlots.Length < 8)
                                    {
                                        position = tempFileStream.Position;
                                        tempFileStream.Write(buffer);
                                    }
                                    else
                                    {
                                        // TODO: Use Bytes.ReadEthUInt64
                                        position = (long)Bytes.ReadEthUInt64(freeSlots.Slice(0, 8));
                                        freeSlots = freeSlots.Slice(8);
                                        _logIndexDb.PutSpan(metaDataKey, freeSlots);
                                    }
                                    RandomAccess.Write(tempFileHandle, bufferForBlockNum, position + slotSize * 4);

                                    location[0] = (byte)FileType.TEMP;
                                    BinaryPrimitives.WriteInt64BigEndian(location.Slice(1), position);
                                    BinaryPrimitives.WriteInt32BigEndian(location.Slice(9), slotSize + 1);
                                    BinaryPrimitives.WriteInt32BigEndian(location.Slice(13), lastBlockNumber); ;

                                    // creating a new index for the address
                                    BinaryPrimitives.WriteInt32BigEndian(keyBytes.Slice(20), blockNumber);
                                    _logIndexDb.PutSpan(keyBytes, location);
                                }
                                else
                                {
                                    // if a temp file alr exists
                                    location = lastEntryForAddress.Value;

                                    byte file = location[0];
                                    position = BinaryPrimitives.ReadInt64BigEndian(location.Slice(1, 8));
                                    slotSize = BinaryPrimitives.ReadInt32BigEndian(location.Slice(9, 4));
                                    lastBlockNumber = BinaryPrimitives.ReadInt32BigEndian(location.Slice(13, 4));

                                    if (lastBlockNumber == blockNumber)
                                    {
                                        continue;
                                    }

                                    lastBlockNumber = blockNumber;

                                    RandomAccess.Write(tempFileHandle, BitConverter.GetBytes(blockNumber), position + slotSize * 4);

                                    //TODO make a more readable stuff to check if we have reached the max slot size for tempfile
                                    if (slotSize + 1 == bufferSize / 4)
                                    {

                                        RandomAccess.Read(tempFileHandle, blockNumbers.AsSpan(), position);
                                        Span<int> blockNumbersInt = MemoryMarshal.Cast<byte, int>(blockNumbers.AsSpan());

                                        location[0] = (byte)FileType.FINAL;
                                        BinaryPrimitives.WriteInt64BigEndian(location.Slice(1), finalizedFileStream.Position);
                                        BinaryPrimitives.WriteInt32BigEndian(location.Slice(9), 1000);
                                        BinaryPrimitives.WriteInt32BigEndian(location.Slice(13), lastBlockNumber);

                                        _logIndexDb.PutSpan(lastEntryForAddress.Key, location);

                                        fixed (int* @in = &MemoryMarshal.GetReference<int>(blockNumbersInt))
                                        fixed (byte* @out = &MemoryMarshal.GetReference<byte>(bufferForEncodedInts))
                                        fixed (int* @out_dec = &MemoryMarshal.GetReference<int>(decodedBlockNumbers))
                                        {
                                            TurboPFor.p4ndenc128v32(@in, blockNumbersInt.Length, @out);
                                            finalizedFileStream.Write(bufferForEncodedInts);

                                            TurboPFor.p4nddec128v32(@out, 1000, @out_dec);
                                        }


                                        //TODO: avoid allocation newFreeSlots with ArrayBoolList
                                        Span<byte> freeSlots = _logIndexDb.GetSpan(metaDataKey);
                                        Span<byte> newFreeSlots = new byte[freeSlots.Length + 8];
                                        freeSlots.CopyTo(newFreeSlots);
                                        BitConverter.GetBytes(position).CopyTo(newFreeSlots.Slice(freeSlots.Length));
                                        _logIndexDb.PutSpan(metaDataKey, newFreeSlots);
                                    }
                                    else
                                    {
                                        //maybe a method to build location?
                                        location[0] = (byte)FileType.TEMP;
                                        BitConverter.GetBytes(slotSize + 1).CopyTo(location.Slice(9));
                                        BitConverter.GetBytes(lastBlockNumber).CopyTo(location.Slice(13));

                                        _logIndexDb.PutSpan(lastEntryForAddress.Key, location);
                                    }
                                }
                            }
                        }
                    }
                    blocksProcessed++;
                }
            }
            finally
            {
                tempFileHandle.Dispose();
                finalizedFileHandle.Dispose();
                finalizedFileStream.Dispose();
                tempFileStream.Dispose();
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
public static class ExtensionMethods
{
    public static byte[] ToBytes(this string str) => Encoding.UTF8.GetBytes(str);
    public static byte[] ToBytes(this int num) => BitConverter.GetBytes(num);
    public static byte[] ToBytes(this long num) => BitConverter.GetBytes(num);
}
