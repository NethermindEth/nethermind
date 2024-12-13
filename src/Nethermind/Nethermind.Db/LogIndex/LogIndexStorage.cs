using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db
{
    public class LogIndexStorage : ILogIndexStorage
    {
        private const string FolderName = "log-index";
        private const string TempFileName = "temp_index.bin";
        private const string FinalFileName = "finalized_index.bin";

        private static readonly byte[] FreePagesKey = "freePages"u8.ToArray();

        public string TempFilePath { get; }
        public string FinalFilePath { get; }

        private readonly SafeFileHandle _tempFileHandle;
        private readonly SafeFileHandle _finalFileHandle;
        private readonly FileStream _tempFileStream;
        private readonly FileStream _finalFileStream;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly IDb _mainDb;
        private readonly ILogger _logger;
        private const int FixedLength = 4096;

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

            _tempFileHandle = File.OpenHandle(TempFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            _finalFileHandle = File.OpenHandle(FinalFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            _tempFileStream = new(_tempFileHandle, FileAccess.ReadWrite);
            _finalFileStream = new(_finalFileHandle, FileAccess.ReadWrite);

            _addressDb = columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDb = columnsDb.GetColumnDb(LogIndexColumns.Topics);
            _mainDb = columnsDb.GetColumnDb(LogIndexColumns.Default);
        }

        public void Dispose()
        {
            _tempFileStream.Dispose();
            _finalFileStream.Dispose();
            _tempFileHandle.Dispose();
            _finalFileHandle.Dispose();
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

            //TODO: rework to use SEekForPrev
            iterator.Seek(keyPrefix);

            byte[] indexBuffer = ArrayPool<byte>.Shared.Rent(FixedLength);

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
                        SafeFileHandle fileHandle = indexInfo.IsTemp ? _tempFileHandle : _finalFileHandle;
                        var data = LoadData(fileHandle, indexInfo, indexBuffer.AsSpan());


                        int[] decompressedBlockNumbers;
                        if (indexInfo.IsTemp)
                        {
                            decompressedBlockNumbers = MemoryMarshal.Cast<byte, int>(data).ToArray();
                        }
                        else
                        {
                            decompressedBlockNumbers = new int[FixedLength / 4];
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

        public SetReceiptsStats SetReceipts(int blockNumber, TxReceipt[] receipts, bool isBackwardSync, CancellationToken cancellationToken)
        {
            return SetReceipts([(blockNumber, receipts)], isBackwardSync, cancellationToken);
        }

        public SetReceiptsStats SetReceipts(ReadOnlySpan<(int blockNumber, TxReceipt[] receipts)> batch, bool isBackwardSync, CancellationToken cancellationToken)
        {
            var stats = new SetReceiptsStats { BlocksAdded = batch.Length };
            var addressIndexes = new Dictionary<byte[], IndexInfo>(Bytes.EqualityComparer);
            var topicIndexes = new Dictionary<byte[], IndexInfo>(Bytes.EqualityComparer);

            var logsProcessed = 0;

            Span<byte> blockNumberBytes = stackalloc byte[4];
            byte[] indexBuffer = ArrayPool<byte>.Shared.Rent(FixedLength);

            try
            {
                foreach ((var blockNumber, TxReceipt[] receipts) in batch)
                {
                    if (!receipts.Any())
                        continue;

                    foreach (TxReceipt receipt in receipts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        stats.TxAdded++;

                        if (receipt.Logs == null)
                            continue;

                        foreach (LogEntry log in receipt.Logs)
                        {
                            stats.LogsAdded++;

                            // Handle address logs
                            byte[] addressKey = log.Address.Bytes;
                            if (!addressIndexes.TryGetValue(addressKey, out IndexInfo addressIndexInfo))
                            {
                                addressIndexInfo = GetOrCreateTempIndex(_addressDb, addressKey, blockNumber, stats);
                                addressIndexInfo.Lock();
                                addressIndexes[addressKey] = addressIndexInfo;
                            }

                            ProcessLog(blockNumber, addressIndexInfo, blockNumberBytes, indexBuffer.AsSpan(), addressKey, _addressDb,
                                addressIndexes);

                            // Handle topic logs
                            foreach (Hash256 topic in log.Topics)
                            {
                                stats.TopicsAdded++;

                                Span<byte> topicKey = topic.Bytes;
                                var topicKeyArray = topicKey.ToArray();
                                if (!topicIndexes.TryGetValue(topicKeyArray, out IndexInfo topicIndexInfo))
                                {
                                    topicIndexInfo = GetOrCreateTempIndex(_topicsDb, topicKey.ToArray(), blockNumber, stats);
                                    topicIndexInfo.Lock();
                                    topicIndexes[topicKeyArray] = topicIndexInfo;
                                }

                                ProcessLog(blockNumber, topicIndexInfo, blockNumberBytes, indexBuffer.AsSpan(), topicKeyArray, _topicsDb,
                                    topicIndexes);
                            }

                            logsProcessed++;
                        }
                    }

                    _lastKnownBlock = Math.Max(_lastKnownBlock, blockNumber);
                }
            }
            finally
            {
                FinalizeIndexes(_addressDb, addressIndexes, blockNumberBytes);
                FinalizeIndexes(_topicsDb, topicIndexes, blockNumberBytes);
                ArrayPool<byte>.Shared.Return(indexBuffer);
            }

            return stats;
        }

        private void ProcessLog(int blockNumber, IndexInfo indexInfo, Span<byte> blockNumberBytes, Span<byte> indexBuffer, byte[] key, IDb db,
            Dictionary<byte[], IndexInfo> indexDictionary)
        {
            if (blockNumber <= indexInfo.LastBlockNumber)
            {
                return;
            }

            long position = indexInfo.Offset + indexInfo.Length * 4;
            BinaryPrimitives.WriteInt32LittleEndian(blockNumberBytes, blockNumber);
            RandomAccess.Write(_tempFileHandle, blockNumberBytes, position);
            indexInfo.Length++;
            indexInfo.LastBlockNumber = blockNumber;

            if (!indexInfo.IsReadyToFinalize())
                return;

            ReadOnlySpan<byte> data = LoadData(_tempFileHandle, indexInfo, indexBuffer);
            ReadOnlySpan<byte> compressed = Compress(data, indexBuffer);
            long offset = Append(_finalFileStream, compressed);
            // TODO: decrease temp file

            Span<byte> dbKey = stackalloc byte[key.Length + sizeof(int)];

            CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(_tempFileHandle, blockNumberBytes), dbKey);
            db.PutSpan(dbKey, CreateIndexValue(offset, compressed.Length, false, indexInfo.LastBlockNumber));

            indexDictionary.Remove(key);

            AddFreePage(indexInfo);
        }

        private void FinalizeIndexes(IDb db, Dictionary<byte[], IndexInfo> indexes, Span<byte> blockNumberBytes)
        {
            if (indexes.Count == 0)
                return;

            Span<byte> dbKey = stackalloc byte[indexes.First().Key.Length + sizeof(int)];

            foreach (IndexInfo? indexInfo in indexes.Values)
            {
                CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(_tempFileHandle, blockNumberBytes), dbKey);
                db.PutSpan(dbKey, CreateIndexValue(indexInfo.Offset, indexInfo.Length, indexInfo.IsTemp, indexInfo.LastBlockNumber));

                indexInfo.Unlock();
            }
        }

        /// <summary>
        /// Either finds existing temporary index for the given key and block number
        /// or
        /// </summary>
        private IndexInfo GetOrCreateTempIndex(IDb db, byte[] key, int blockNumber, SetReceiptsStats stats)
        {
            using IIterator<byte[], byte[]> iterator = db.GetIterator(true);
            Span<byte> dbKey = stackalloc byte[key.Length + sizeof(int)];

            CreateDbKey(key, blockNumber, dbKey);

            var watch = Stopwatch.StartNew();
            iterator.SeekForPrev(dbKey);

            if (iterator.Valid() && // Found key is less than or equal to the requested one
                iterator.Key().AsSpan()[..key.Length].SequenceEqual(key))
            {
                stats.SeekForPrevHit.Include(watch.Elapsed);

                IndexInfo latestIndex = new(key, iterator.Value());

                if (latestIndex.IsTemp)
                    return latestIndex;
            }
            else
            {
                stats.SeekForPrevMiss.Include(watch.Elapsed);
            }

            long freePage = GetFreePage() ?? GrowFile(_tempFileStream, FixedLength);
            return new(key, freePage, 0, true, 0);
        }

        private void AddFreePage(IndexInfo indexInfo)
        {
            lock (_mainDb)
            {
                byte[] freePages = _mainDb.Get(FreePagesKey);
                long newFreePage = indexInfo.Offset;

                // Prepare new array with the old free pages plus the new one
                byte[] newFreePages = ArrayPool<byte>.Shared.Rent((freePages?.Length ?? 0) + sizeof(long));

                try
                {
                    if (freePages != null)
                    {
                        freePages.CopyTo(newFreePages, 0);
                    }

                    // Append the new free page
                    BinaryPrimitives.WriteInt64BigEndian(newFreePages.AsSpan(freePages?.Length ?? 0), newFreePage);

                    // Save the updated free pages back to the database
                    _mainDb.PutSpan(FreePagesKey, newFreePages.AsSpan(0, (freePages?.Length ?? 0) + sizeof(long)));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(newFreePages);
                }
            }
        }

        private long? GetFreePage()
        {
            byte[] freePages = _mainDb.Get(FreePagesKey);

            if (freePages is { Length: >= sizeof(long) })
            {
                // Extract the last 8 bytes as the free page
                long freePage = BinaryPrimitives.ReadInt64BigEndian(freePages.AsSpan(freePages.Length - sizeof(long)));

                // Update the freePages array by removing the last 8 bytes
                _mainDb.PutSpan(FreePagesKey, freePages.AsSpan(0, freePages.Length - sizeof(long)));

                return freePage;
            }

            return null;
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
            int count = indexInfo.IsTemp ? indexInfo.Length * 4 : indexInfo.Length;
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
            return buffer.Slice(0, blockNumbers.Length * sizeof(int)); // Adjust le ngth if necessary
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

        private long GrowFile(FileStream fileStream, int length)
        {
            long originalLength = fileStream.Length;
            fileStream.SetLength(originalLength + length);
            return originalLength;
        }

        // TODO: make ref struct?
        private class IndexInfo
        {
            public byte[] Key { get; }
            public long Offset { get; set; }
            public bool IsTemp { get; set; }
            public int Length { get; set; }
            public int LastBlockNumber { get; set; }
            private readonly Lock _lock = new();

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

            public bool IsReadyToFinalize() => Length >= FixedLength / 4;

            public void Lock() => _lock.Enter();

            public void Unlock() => _lock.Exit();

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
