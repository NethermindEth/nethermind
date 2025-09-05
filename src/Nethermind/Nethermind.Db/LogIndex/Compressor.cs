// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db;

partial class LogIndexStorage
{
    private interface ICompressor
    {
        PostMergeProcessingStats Stats { get; }
        PostMergeProcessingStats GetAndResetStats();
        bool TryEnqueueAddress(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue);
        bool TryEnqueueTopic(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue);
        Task EnqueueAddressAsync(byte[] dbKey);
        Task EnqueueTopicAsync(byte[] dbKey);
        void WaitUntilEmpty();
        Task StopAsync();
    }

    private class Compressor : ICompressor
    {
        public const int MinLengthToCompress = 128 * BlockNumSize;

        // A lot of duplicates in case of a regular Channel
        // TODO: find a better way to guarantee uniqueness?
        private readonly ConcurrentDictionary<byte[], bool> _compressQueue = new(Bytes.EqualityComparer);
        private readonly LogIndexStorage _storage;
        private readonly ILogger _logger;
        private readonly ActionBlock<byte[]> _addressBlock;
        private readonly ActionBlock<byte[]> _topicBlock;
        private readonly ManualResetEventSlim _queueEmptyEvent = new(true); // TODO: fix event being used for both blocks

        private PostMergeProcessingStats _stats = new();
        public PostMergeProcessingStats Stats => _stats;

        public PostMergeProcessingStats GetAndResetStats()
        {
            _stats.QueueLength = _addressBlock.InputCount;
            return Interlocked.Exchange(ref _stats, new());
        }

        public Compressor(LogIndexStorage storage, ILogger logger, int ioParallelism)
        {
            _storage = storage;
            _logger = logger;

            if (ioParallelism < 1) throw new ArgumentException("IO parallelism degree must be a positive value.", nameof(ioParallelism));
            _addressBlock = new(CompressAddress, new() { MaxDegreeOfParallelism = ioParallelism, BoundedCapacity = 10_000 });
            _topicBlock = new(CompressTopic, new() { MaxDegreeOfParallelism = ioParallelism, BoundedCapacity = 10_000 });
        }

        public bool TryEnqueueAddress(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue) => TryEnqueue(_addressBlock, dbKey, dbValue);
        public bool TryEnqueueTopic(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue) => TryEnqueue(_topicBlock, dbKey, dbValue);

        private bool TryEnqueue(ActionBlock<byte[]> block, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse - may not initialized yet, compression can be started from the constructor
            // TODO: add to queue, but start processing later?
            if (_storage._columnsDb is null)
                return false;

            if (dbValue.Length < MinLengthToCompress)
                return false;

            var dbKeyArr = dbKey.ToArray();
            if (!_compressQueue.TryAdd(dbKeyArr, true))
                return false;

            if (_addressBlock.Post(dbKeyArr))
                return true;

            _compressQueue.TryRemove(dbKeyArr, out _);
            return false;
        }

        public Task EnqueueAddressAsync(byte[] dbKey) => EnqueueAsync(_addressBlock, dbKey);
        public Task EnqueueTopicAsync(byte[] dbKey) => EnqueueAsync(_topicBlock, dbKey);

        private async Task EnqueueAsync(ActionBlock<byte[]> block, byte[] dbKey)
        {
            await block.SendAsync(dbKey);
            _queueEmptyEvent.Reset();
        }

        public void WaitUntilEmpty() => _queueEmptyEvent.Wait();

        public Task StopAsync()
        {
            _addressBlock.Complete();
            return _addressBlock.Completion;
        }

        private void CompressAddress(byte[] dbKey)
        {
            CompressValue(_storage._addressDb, dbKey);
            Interlocked.Increment(ref _stats.CompressedAddressKeys);
        }

        private void CompressTopic(byte[] dbKey)
        {
            CompressValue(_storage._topicsDb, dbKey);
            Interlocked.Increment(ref _stats.CompressedTopicKeys);
        }

        // TODO: optimize allocations
        private void CompressValue(IDb db, byte[] dbKey)
        {
            try
            {
                var execTimestamp = Stopwatch.GetTimestamp();

                var timestamp = Stopwatch.GetTimestamp();
                Span<byte> dbValue = db.Get(dbKey);
                _stats.GettingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                // Do not compress blocks that can be reorged, as compressed data is immutable
                if (!UseBackwardSyncFor(dbKey))
                    dbValue = _storage.RemoveReorgableBlocks(dbValue);

                if (dbValue.Length < MinLengthToCompress)
                    return; // TODO: check back later

                var truncateBlock = GetValLastBlockNum(dbValue);

                ReverseBlocksIfNeeded(dbValue);

                var postfixBlock = GetValBlockNum(dbValue);

                Span<byte> key = SpecialPostfix.CutFrom(dbKey);
                Span<byte> dbKeyComp = new byte[key.Length + BlockNumSize];
                key.CopyTo(dbKeyComp);
                SetKeyBlockNum(dbKeyComp, postfixBlock);

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

                _stats.Execution.Include(Stopwatch.GetElapsedTime(execTimestamp));
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Error during post-merge compression.", ex);
            }
            finally
            {
                _compressQueue.TryRemove(dbKey, out _);

                if (_addressBlock.InputCount == 0)
                    _queueEmptyEvent.Set();
            }
        }
    }

    public class NoOpCompressor : ICompressor
    {
        public PostMergeProcessingStats Stats { get; } = new();
        public PostMergeProcessingStats GetAndResetStats() => Stats;
        public bool TryEnqueueAddress(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue) => false;
        public bool TryEnqueueTopic(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue) => false;
        public Task EnqueueAddressAsync(byte[] dbKey) => Task.CompletedTask;
        public Task EnqueueTopicAsync(byte[] dbKey) => Task.CompletedTask;
        public void WaitUntilEmpty() { }
        public Task StopAsync() => Task.CompletedTask;
    }
}
