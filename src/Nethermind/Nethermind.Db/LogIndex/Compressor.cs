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
        int MinLengthToCompress { get; }
        PostMergeProcessingStats Stats { get; }
        PostMergeProcessingStats GetAndResetStats();
        bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue);
        Task EnqueueAsync(int? topicIndex, byte[] dbKey);
        Task WaitUntilEmptyAsync(TimeSpan waitTime = default, CancellationToken cancellationToken = default);
        void Start();
        Task StopAsync();
    }

    private class Compressor : ICompressor
    {
        public int MinLengthToCompress { get; }

        // A lot of duplicates in case of a regular Channel
        // TODO: find a better way to guarantee uniqueness?
        private readonly ConcurrentDictionary<byte[], bool> _compressQueue = new(Bytes.EqualityComparer);
        private readonly LogIndexStorage _storage;
        private readonly ILogger _logger;
        private readonly ActionBlock<(int?, byte[])> _block;
        private readonly ManualResetEventSlim _startEvent = new(false);
        private readonly ManualResetEventSlim _queueEmptyEvent = new(true); // TODO: fix event being used for both blocks

        private PostMergeProcessingStats _stats = new();
        public PostMergeProcessingStats Stats => _stats;

        public PostMergeProcessingStats GetAndResetStats()
        {
            _stats.QueueLength = _block.InputCount;
            return Interlocked.Exchange(ref _stats, new());
        }

        public Compressor(LogIndexStorage storage, ILogger logger, int compressionDistance, int parallelism)
        {
            _storage = storage;
            _logger = logger;

            MinLengthToCompress = compressionDistance * BlockNumSize;

            if (parallelism < 1) throw new ArgumentException("Compression parallelism degree must be a positive value.", nameof(parallelism));
            _block = new(x => CompressValue(x.Item1, x.Item2), new() { MaxDegreeOfParallelism = parallelism, BoundedCapacity = 10_000 });
        }

        public bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue)
        {
            if (dbValue.Length < MinLengthToCompress)
                return false;

            var dbKeyArr = dbKey.ToArray();
            if (!_compressQueue.TryAdd(dbKeyArr, true))
                return false;

            if (_block.Post((topicIndex, dbKeyArr)))
                return true;

            _compressQueue.TryRemove(dbKeyArr, out _);
            return false;
        }

        public async Task EnqueueAsync(int? topicIndex, byte[] dbKey)
        {
            await _block.SendAsync((topicIndex, dbKey));
            _queueEmptyEvent.Reset();
        }

        public Task WaitUntilEmptyAsync(TimeSpan waitTime, CancellationToken cancellationToken) =>
            _queueEmptyEvent.WaitHandle.WaitOneAsync(waitTime, cancellationToken);

        public void Start() => _startEvent.Set();

        public Task StopAsync()
        {
            _block.Complete();
            return _block.Completion;
        }

        // TODO: optimize allocations
        private void CompressValue(int? topicIndex, byte[] dbKey)
        {
            try
            {
                _startEvent.Wait();

                var execTimestamp = Stopwatch.GetTimestamp();
                IDb db = _storage.GetDb(topicIndex);

                var timestamp = Stopwatch.GetTimestamp();
                Span<byte> dbValue = db.Get(dbKey);
                _stats.GettingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                // Do not compress blocks that can be reorged, as compressed data is immutable
                if (!UseBackwardSyncFor(dbKey))
                    dbValue = _storage.RemoveReorgableBlocks(dbValue);

                if (dbValue.Length < MinLengthToCompress)
                    return; // TODO: check back later?

                var truncateBlock = GetValLastBlockNum(dbValue);

                ReverseBlocksIfNeeded(dbValue);

                var postfixBlock = GetValBlockNum(dbValue);

                ReadOnlySpan<byte> key = ExtractKey(dbKey);
                Span<byte> dbKeyComp = new byte[key.Length + BlockNumSize];
                key.CopyTo(dbKeyComp);
                SetKeyBlockNum(dbKeyComp[key.Length..], postfixBlock);

                timestamp = Stopwatch.GetTimestamp();
                dbValue = _storage.CompressDbValue(dbValue);
                _stats.CompressingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                // Put compressed value at a new key and clear the uncompressed one
                timestamp = Stopwatch.GetTimestamp();
                using (IWriteBatch batch = db.StartWriteBatch())
                {
                    batch.PutSpan(dbKeyComp, dbValue);
                    batch.Merge(dbKey, MergeOps.Create(MergeOp.TruncateOp, truncateBlock));
                }

                _stats.PuttingValues.Include(Stopwatch.GetElapsedTime(timestamp));

                if (topicIndex is null)
                    Interlocked.Increment(ref _stats.CompressedAddressKeys);
                else
                    Interlocked.Increment(ref _stats.CompressedTopicKeys);

                _stats.Execution.Include(Stopwatch.GetElapsedTime(execTimestamp));
            }
            catch (Exception ex) // TODO: forward any error to storage or caller
            {
                if (_logger.IsError) _logger.Error("Error during post-merge compression.", ex);
            }
            finally
            {
                _compressQueue.TryRemove(dbKey, out _);

                if (_block.InputCount == 0) // TODO: take processing items into account
                    _queueEmptyEvent.Set();
            }
        }
    }

    public class NoOpCompressor : ICompressor
    {
        public int MinLengthToCompress => 256;
        public PostMergeProcessingStats Stats { get; } = new();
        public PostMergeProcessingStats GetAndResetStats() => Stats;
        public bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue) => false;
        public Task EnqueueAsync(int? topicIndex, byte[] dbKey) => Task.CompletedTask;
        public Task WaitUntilEmptyAsync(TimeSpan waitTime, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
    }
}
