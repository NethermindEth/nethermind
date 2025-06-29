// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db;

public partial class LogIndexStorage
{
    private class Compressor
    {
        public const int MinLengthToCompress = 128 * BlockNumSize;

        // A lot of duplicates in case of a regular Channel
        // TODO: find a better way to guarantee uniqueness?
        private readonly ConcurrentDictionary<byte[], bool> _compressQueue = new(Bytes.EqualityComparer);
        private readonly LogIndexStorage _storage;
        private readonly ILogger _logger;
        private readonly ActionBlock<(byte[] key, byte[] value)> _processingBlock;
        private readonly ManualResetEventSlim _queueEmptyEvent = new(true);

        private PostMergeProcessingStats _stats = new();
        public PostMergeProcessingStats GetAndResetStats()
        {
            _stats.QueueLength = _processingBlock.InputCount;
            return Interlocked.Exchange(ref _stats, new());
        }

        public Compressor(LogIndexStorage storage, ILogger logger, int ioParallelism)
        {
            _storage = storage;
            _logger = logger;

            if (ioParallelism < 1) throw new ArgumentException("IO parallelism degree must be a positive value.", nameof(ioParallelism));
            _processingBlock = new(CompressValue, new() { MaxDegreeOfParallelism = ioParallelism, BoundedCapacity = 10_000 });
        }

        public bool TryEnqueue(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue)
        {
            if (dbValue.Length < MinLengthToCompress)
                return false;

            var dbKeyArr = dbKey.ToArray();
            if (_compressQueue.TryAdd(dbKeyArr, true))
                return false;

            var dbValueArr = dbValue.ToArray();
            if (_processingBlock.Post((dbKeyArr, dbValueArr)))
                return true;

            _compressQueue.TryRemove(dbKeyArr, out _);
            return false;
        }

        public async Task EnqueueAsync(byte[] dbKey, byte[] dbValue)
        {
            await _processingBlock.SendAsync((dbKey, dbValue));
            _queueEmptyEvent.Reset();
        }

        public void WaitUntilEmpty() => _queueEmptyEvent.Wait();

        public Task StopAsync()
        {
            _processingBlock.Complete();
            return _processingBlock.Completion;
        }

        // TODO: log errors
        // TODO: optimize allocations
        // TODO: use WriteBatch for atomicity
        private void CompressValue((byte[] key, byte[] value) arg)
        {
            Span<byte> dbKey = arg.key;
            Span<byte> dbValue = arg.value;

            try
            {
                var execTimestamp = Stopwatch.GetTimestamp();

                IDb db = _storage.GetDbByKeyLength(dbKey.Length, out var prefixLength);

                var timestamp = Stopwatch.GetTimestamp();

                _stats.GettingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                // Do not compress blocks that can be reorged, as compressed data is immutable
                if (!UseBackwardSyncFor(dbKey))
                    dbValue = _storage.RemoveReorgableBlocks(dbValue);

                if (dbValue.Length < MinLengthToCompress)
                    return; // TODO: check back later

                var firstBlock = GetValBlockNum(dbValue);
                var truncateBlock = GetValLastBlockNum(dbValue);
                ReverseBlocksIfNeeded(dbValue);

                var dbKeyComp = new byte[prefixLength + BlockNumSize];
                dbKey[..prefixLength].CopyTo(dbKeyComp);
                SetKeyBlockNum(dbKeyComp, firstBlock);

                timestamp = Stopwatch.GetTimestamp();
                dbValue = CompressDbValue(dbValue);
                _stats.CompressingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                // Put compressed value at a new key and clear the uncompressed one
                timestamp = Stopwatch.GetTimestamp();
                using (IWriteBatch batch = db.StartWriteBatch())
                {
                    batch.PutSpan(dbKeyComp, dbValue);
                    batch.Merge(dbKey, MergeOps.Create(MergeOp.TruncateOp, truncateBlock));
                }

                _stats.PuttingValues.Include(Stopwatch.GetElapsedTime(timestamp));

                if (prefixLength == Address.Size)
                    Interlocked.Increment(ref _stats.CompressedAddressKeys);
                else if (prefixLength == Hash256.Size)
                    Interlocked.Increment(ref _stats.CompressedTopicKeys);

                _stats.Execution.Include(Stopwatch.GetElapsedTime(execTimestamp));
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Error during post-merge compression.", ex);
            }
            finally
            {
                _compressQueue.TryRemove(arg.key, out _);

                if (_processingBlock.InputCount == 0)
                    _queueEmptyEvent.Set();
            }
        }
    }
}
