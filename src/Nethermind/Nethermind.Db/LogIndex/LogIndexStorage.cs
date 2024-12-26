using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db
{
    // TODO: try to store block numbers in RocksDB first, move to a page only when needed
    // TODO: try to increase page size gradually (use different files for different page sizes?)
    public sealed class LogIndexStorage : ILogIndexStorage
    {
        private const string FolderName = "log-index";
        private const string TempFileName = "temp_index.bin";
        private const string FinalFileName = "finalized_index.bin";

        public string TempFilePath { get; }
        public string FinalFilePath { get; }

        private readonly SafeFileHandle _finalFileHandle;
        private readonly FileStream _finalFileStream;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly ILogger _logger;
        private const int PageSize = 4096;

        private readonly IAsyncFilePagesPool _tempPagesPool;

        // TODO: ensure class is singleton
        public LogIndexStorage(IColumnsDb<LogIndexColumns> columnsDb, ILogger logger, string baseDbPath)
        {
            TempFilePath = Path.Combine(baseDbPath, FolderName, TempFileName);
            FinalFilePath = Path.Combine(baseDbPath, FolderName, FinalFileName);

            _logger = logger;

            _logger.Info($"Temp file path: {TempFilePath}");
            _logger.Info($"Final file path: {FinalFilePath}");

            // TODO: use IFileSystem
            Directory.CreateDirectory(Path.Combine(baseDbPath, FolderName));

            _finalFileHandle = File.OpenHandle(FinalFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            _finalFileStream = new(_finalFileHandle, FileAccess.ReadWrite);

            _addressDb = columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDb = columnsDb.GetColumnDb(LogIndexColumns.Topics);

            IDb tempPagesDb = columnsDb.GetColumnDb(LogIndexColumns.Default);
            _tempPagesPool = new AsyncFilePagesPool(TempFilePath, tempPagesDb, PageSize)
            {
                AllocatedPagesPoolSize = 2048,
                ReturnedPagesPoolSize = 2048
            };
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            await _finalFileStream.DisposeAsync();
            _finalFileHandle.Dispose();

            await _tempPagesPool.StopAsync();
            await _tempPagesPool.DisposeAsync();
        }

        private int _lastKnownBlock = -1;

        public int GetLastKnownBlockNumber() => _lastKnownBlock;

        public IEnumerable<int> GetBlockNumbersFor(Address address, int from, int to)
        {
            return GetBlockNumbersFor(_addressDb, address.Bytes, from, to);
        }

        public IEnumerable<int> GetBlockNumbersFor(Hash256 topic, int from, int to)
        {
            return GetBlockNumbersFor(_topicsDb, topic.Bytes.ToArray(), from, to);
        }

        private IEnumerable<int> GetBlockNumbersFor(IDb db, byte[] keyPrefix, int from, int to)
        {
            using IIterator<byte[], byte[]> iterator = db.GetIterator(true);

            //TODO: rework to use SeekForPrev
            iterator.Seek(keyPrefix);

            byte[] indexBuffer = ArrayPool<byte>.Shared.Rent(PageSize);

            try
            {
                while (iterator.Valid() && iterator.Key().AsSpan()[..keyPrefix.Length].SequenceEqual(keyPrefix))
                {
                    ReadOnlySpan<byte> firstKey = iterator.Key().AsSpan();

                    int currentLowestBlockNumber = BinaryPrimitives.ReadInt32BigEndian(firstKey.Slice(keyPrefix.Length));

                    iterator.Next();
                    int? nextLowestBlockNumber;
                    if (iterator.Valid() && iterator.Key().AsSpan()[..keyPrefix.Length].SequenceEqual(keyPrefix))
                    {
                        ReadOnlySpan<byte> nextKey = iterator.Key().AsSpan();
                        nextLowestBlockNumber = BinaryPrimitives.ReadInt32BigEndian(nextKey.Slice(keyPrefix.Length));
                    }
                    else
                    {
                        nextLowestBlockNumber = int.MaxValue;
                    }

                    if (nextLowestBlockNumber > from && currentLowestBlockNumber <= to)
                    {
                        IndexInfo indexInfo = new(keyPrefix.ToArray(), db.Get(firstKey));
                        SafeFileHandle fileHandle = indexInfo.IsTemp ? _tempPagesPool.FileHandle : _finalFileHandle;
                        ReadOnlySpan<byte> data = LoadData(fileHandle, indexInfo, indexBuffer.AsSpan());

                        int[] decompressedBlockNumbers;
                        if (indexInfo.IsTemp)
                        {
                            decompressedBlockNumbers = MemoryMarshal.Cast<byte, int>(data).ToArray();
                        }
                        else
                        {
                            decompressedBlockNumbers = new int[PageSize / 4];
                            Decompress(data, decompressedBlockNumbers);
                        }

                        int startIndex = BinarySearch(decompressedBlockNumbers, from);
                        if (startIndex < 0)
                        {
                            startIndex = ~startIndex;
                        }

                        for (int i = startIndex; i < decompressedBlockNumbers.Length; i++)
                        {
                            int block = decompressedBlockNumbers[i];
                            if (block > to)
                                yield break;

                            yield return block;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(indexBuffer);
            }
        }

        // TODO: try to minimize number of allocations
        private Dictionary<byte[], List<int>>? BuildProcessingDictionary(
            (int blockNumber, TxReceipt[] receipts)[] batch, SetReceiptsStats stats, CancellationToken cancellationToken
        )
        {
            if (batch[^1].blockNumber <= _lastKnownBlock)
                return null;

            var blockNumsByKey = new Dictionary<byte[], List<int>>(Bytes.EqualityComparer);
            foreach ((var blockNumber, TxReceipt[] receipts) in batch)
            {
                if (blockNumber <= _lastKnownBlock)
                    continue;

                stats.BlocksAdded++;

                foreach (TxReceipt receipt in receipts)
                {
                    stats.TxAdded++;

                    if (receipt.Logs == null)
                        continue;

                    foreach (LogEntry log in receipt.Logs)
                    {
                        stats.LogsAdded++;

                        cancellationToken.ThrowIfCancellationRequested();

                        List<int> addressNums = blockNumsByKey.GetOrAdd(log.Address.Bytes, _ => new(1));

                        if (addressNums.Count == 0 || addressNums[^1] != blockNumber)
                            addressNums.Add(blockNumber);

                        foreach (Hash256 topic in log.Topics)
                        {
                            stats.TopicsAdded++;

                            cancellationToken.ThrowIfCancellationRequested();

                            List<int> topicNums = blockNumsByKey.GetOrAdd(topic.Bytes.ToArray(), _ => new(1));

                            if (topicNums.Count == 0 || topicNums[^1] != blockNumber)
                                topicNums.Add(blockNumber);
                        }
                    }
                }
            }

            stats.KeysCount = blockNumsByKey.Count;
            return blockNumsByKey;
        }

        public Task<SetReceiptsStats> SetReceiptsAsync(int blockNumber, TxReceipt[] receipts, bool isBackwardSync,
            CancellationToken cancellationToken)
        {
            return SetReceiptsAsync([(blockNumber, receipts)], isBackwardSync, cancellationToken);
        }

        public async Task<SetReceiptsStats> SetReceiptsAsync(
            (int blockNumber, TxReceipt[] receipts)[] batch, bool isBackwardSync, CancellationToken cancellationToken
        )
        {
            await _tempPagesPool.StartAsync();

            var stats = new SetReceiptsStats();

            var watch = Stopwatch.StartNew();
            if (BuildProcessingDictionary(batch, stats, cancellationToken) is not {} dictionary)
                return stats;
            stats.BuildingDictionary.Include(watch.Elapsed);

            var finalizeQueue = Channel.CreateUnbounded<IndexInfo>();
            Task finalizingTask = KeepFinalizingIndexes(finalizeQueue.Reader, cancellationToken);

            try
            {
                // TODO: adjust MaxDegreeOfParallelism
                Parallel.ForEach(dictionary, new() { MaxDegreeOfParallelism = 16 }, pair =>
                {
                    (var key, List<int> blockNums) = pair;
                    IDb db = key.Length switch
                    {
                        Address.Size => _addressDb,
                        Hash256.Size => _topicsDb,
                        var size => throw new($"Unexpected key of {size} bytes.") // Should never happen
                    };

                    IndexInfo? indexInfo = GetOrCreateTempIndex(db, key, blockNums[0], stats);
                    byte[] bytesBuffer = ArrayPool<byte>.Shared.Rent(PageSize);
                    Span<byte> bytes = Span<byte>.Empty;

                    foreach (var blockNum in blockNums)
                    {
                        indexInfo ??= CreateTempIndex(key);

                        if (blockNum <= indexInfo.LastBlockNumber)
                            continue;

                        bytes = bytesBuffer.AsSpan(0, bytes.Length + sizeof(int));
                        BinaryPrimitives.WriteInt32LittleEndian(bytes[^sizeof(int)..], blockNum);

                        if (indexInfo.ByteLengthRemaining == bytes.Length)
                        {
                            WriteBytes(indexInfo, bytes, stats);
                            bytes = Span<byte>.Empty;

                            finalizeQueue.Writer.TryWrite(indexInfo);
                            indexInfo = null;
                        }
                    }

                    if (indexInfo != null)
                    {
                        WriteBytes(indexInfo, bytes, stats);
                        SaveIndex(db, indexInfo);
                    }
                });

                _lastKnownBlock = Math.Max(_lastKnownBlock, batch.Max(b => b.blockNumber));
            }
            catch (Exception exception)
            {
                GC.KeepAlive(exception); // TODO: remove after testing
            }
            finally
            {
                finalizeQueue.Writer.Complete();

                watch = Stopwatch.StartNew();
                await finalizingTask;
                stats.WaitingForFinalization.Include(watch.Elapsed);
            }

            return stats;
        }

        public PagesStats PagesStats => _tempPagesPool.Stats;

        private SafeFileHandle TempFileHandle => _tempPagesPool.FileHandle;

        private async Task KeepFinalizingIndexes(ChannelReader<IndexInfo> reader, CancellationToken cancellationToken)
        {
            byte[] blockNumberBytes = new byte[sizeof(int)];
            byte[] indexBuffer = new byte[PageSize];

            var addressKey = new byte[Address.Size + sizeof(int)];
            var topicKey = new byte[Hash256.Size + sizeof(int)];

            await foreach (IndexInfo indexInfo in reader.ReadAllAsync(cancellationToken))
            {
                ReadOnlySpan<byte> data = LoadData(TempFileHandle, indexInfo, indexBuffer);
                ReadOnlySpan<byte> compressed = Compress(data, indexBuffer);
                long offset = Append(_finalFileStream, compressed);

                (byte[] dbKey, IDb db) = indexInfo.Key.Length switch
                {
                    Address.Size => (addressKey, _addressDb),
                    Hash256.Size => (topicKey, _topicsDb),
                    var size => throw new($"Unexpected index size of {size} bytes.") // Should never happen
                };

                CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(TempFileHandle, blockNumberBytes), dbKey);
                db.PutSpan(dbKey, CreateIndexValue(offset, compressed.Length, false, indexInfo.LastBlockNumber));

                // TODO: improve awaiting
                _tempPagesPool.ReturnPageAsync(indexInfo.Offset).Wait();
            }
        }

        private bool WriteBytes(IndexInfo indexInfo, Span<byte> bytes, SetReceiptsStats stats)
        {
            long position = indexInfo.Position;
            RandomAccess.Write(TempFileHandle, bytes, position);
            indexInfo.Length += bytes.Length / sizeof(int);
            indexInfo.LastBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(bytes[^sizeof(int)..]);

            stats.BytesWritten.Include(bytes.Length);

            return true;
        }

        private void SaveIndex(IDb db, IndexInfo indexInfo)
        {
            Span<byte> blockNumberBytes = stackalloc byte[sizeof(int)];
            Span<byte> dbKey = stackalloc byte[indexInfo.Key.Length + sizeof(int)];
            CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(TempFileHandle, blockNumberBytes), dbKey);
            db.PutSpan(dbKey, CreateIndexValue(indexInfo.Offset, indexInfo.Length, indexInfo.IsTemp, indexInfo.LastBlockNumber));
        }

        private IndexInfo CreateTempIndex(byte[] keyPrefix)
        {
            // TODO: improve awaiting
            long freePage = _tempPagesPool.TakePageAsync().WaitResult();

            // TODO: Save index offset to DB immediately after obtaining the page
            return new(keyPrefix, freePage, 0, true, -1);
        }

        private IndexInfo GetOrCreateTempIndex(IDb db, byte[] keyPrefix, int blockNumber, SetReceiptsStats stats)
        {
            Span<byte> dbKey = stackalloc byte[keyPrefix.Length + sizeof(int)];

            // TODO: check if Seek and a few Next (or using reversed data order) will make use of prefix seek
            byte[] dbPrefix = new byte[dbKey.Length]; // TODO: check if ArrayPool will work (as size is not guaranteed)
            Array.Copy(keyPrefix, dbPrefix, keyPrefix.Length);

            CreateDbKey(keyPrefix, blockNumber, dbKey);

            var options = new IteratorOptions { IsOrdered = true, LowerBound = dbPrefix };
            using IIterator<byte[], byte[]> iterator = db.GetIterator(ref options);

            var watch = Stopwatch.StartNew();
            iterator.SeekForPrev(dbKey);

            if (iterator.Valid() && // Found key is less than or equal to the requested one
                iterator.Key().AsSpan()[..keyPrefix.Length].SequenceEqual(keyPrefix))
            {
                stats.SeekForPrevHit.Include(watch.Elapsed);

                IndexInfo latestIndex = new(keyPrefix, iterator.Value());

                if (latestIndex.IsTemp)
                    return latestIndex;
            }
            else
            {
                stats.SeekForPrevMiss.Include(watch.Elapsed);
            }

            return CreateTempIndex(keyPrefix);
        }

        /// <summary>
        /// Saves a key consisting of the <c>key || block-number</c> byte array to <paramref name="buffer"/>
        /// </summary>
        public void CreateDbKey(ReadOnlySpan<byte> key, int blockNumber, Span<byte> buffer)
        {
            key.CopyTo(buffer);
            BinaryPrimitives.WriteInt32BigEndian(buffer[key.Length..], blockNumber);
        }

        private ReadOnlySpan<byte> LoadData(SafeFileHandle fileHandle, IndexInfo indexInfo, Span<byte> buffer)
        {
            int count = indexInfo.ByteLength;
            RandomAccess.Read(fileHandle, buffer.Slice(0, count), indexInfo.Offset);
            return buffer.Slice(0, count);
        }

        private unsafe void Decompress(ReadOnlySpan<byte> data, ReadOnlySpan<int> decompressedBlockNumbers)
        {
            fixed (byte* dataPtr = data)
            fixed (int* decompressedPtr = decompressedBlockNumbers)
            {
                TurboPFor.p4nddec128v32(dataPtr, decompressedBlockNumbers.Length, decompressedPtr);
            }
        }

        private int BinarySearch(ReadOnlySpan<int> blocks, int from)
        {
            int index = blocks.BinarySearch(from);
            return index < 0 ? ~index : index;
        }

        private unsafe ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> data, Span<byte> buffer)
        {
            ReadOnlySpan<int> blockNumbers = MemoryMarshal.Cast<byte, int>(data);
            fixed (int* blockNumbersPtr = blockNumbers)
            fixed (byte* compressedPtr = buffer)
            {
                TurboPFor.p4ndenc128v32(blockNumbersPtr, blockNumbers.Length, compressedPtr);
            }

            return buffer.Slice(0, blockNumbers.Length * sizeof(int)); // Adjust length if necessary
        }

        private long Append(FileStream fileStream, ReadOnlySpan<byte> data)
        {
            long offset = fileStream.Length;
            fileStream.Seek(offset, SeekOrigin.Begin);
            fileStream.Write(data);
            return offset;
        }

        private byte[] CreateIndexValue(long offset, int count, bool isTemp, int lastBlockNumber)
        {
            Span<byte> value = stackalloc byte[1 + sizeof(long) + 2 * sizeof(int)];
            value[0] = (byte)(isTemp ? FileType.Temp : FileType.Final);
            BinaryPrimitives.WriteInt64BigEndian(value[1..], offset);
            BinaryPrimitives.WriteInt32BigEndian(value[(1 + sizeof(long))..], count);
            BinaryPrimitives.WriteInt32BigEndian(value[(1 + sizeof(long) + sizeof(int))..], lastBlockNumber);
            return value.ToArray();
        }

        // TODO: make ref struct?
        private class IndexInfo
        {
            public byte[] Key { get; }
            public long Offset { get; }
            public bool IsTemp { get; }
            public int Length { get; set; }
            public int LastBlockNumber { get; set; }

            public int ByteLength => IsTemp ? Length * 4 : Length;
            public long Position => Offset + ByteLength;
            public int LengthRemaining => PageSize / 4 - Length;
            public int ByteLengthRemaining => IsTemp ? LengthRemaining * 4 : 0;

            public IndexInfo(byte[] key, long offset, int length, bool isTemp, int lastBlockNumber)
            {
                Key = key;
                Offset = offset;
                Length = length;
                IsTemp = isTemp;
                LastBlockNumber = lastBlockNumber;
            }

            public IndexInfo(byte[] key, Span<byte> value)
            {
                Key = key;
                IsTemp = value[0] == (byte)FileType.Temp;
                Offset = BinaryPrimitives.ReadInt64BigEndian(value[1..]);
                Length = BinaryPrimitives.ReadInt32BigEndian(value[(1 + sizeof(long))..]);
                LastBlockNumber = BinaryPrimitives.ReadInt32BigEndian(value[(1 + sizeof(long) + sizeof(int))..]);
            }

            public bool IsReadyToFinalize() => Length >= PageSize / 4;

            // private readonly Lock _lock = new();
            // public void Lock() => _lock.Enter();
            // public void Unlock() => _lock.Exit();

            private int _lowestBlockNumber = -1;

            public int LowestBlockNumber(SafeFileHandle fileHandle, Span<byte> buffer)
            {
                if (_lowestBlockNumber < 0)
                {
                    RandomAccess.Read(fileHandle, buffer[..4], Offset);
                    _lowestBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(buffer);
                }

                return _lowestBlockNumber;
            }
        }

        private enum FileType : byte
        {
            Temp = 0x01,
            Final = 0x02
        }
    }
}
