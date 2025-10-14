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
    /// <summary>
    /// Does background compression for keys with the number of blocks above the threshold.
    /// </summary>
    private interface ICompressor: IDisposable
    {
        int MinLengthToCompress { get; }
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

        // Used instead of a channel to prevent duplicates
        private readonly ConcurrentDictionary<byte[], bool> _compressQueue = new(Bytes.EqualityComparer);
        private readonly LogIndexStorage _storage;
        private readonly ILogger _logger;
        private readonly ActionBlock<(int?, byte[])> _processing;
        private readonly ManualResetEventSlim _startEvent = new(false);
        private readonly ManualResetEventSlim _queueEmptyEvent = new(true);
        private readonly CancellationTokenSource _cts = new();

        private PostMergeProcessingStats _stats = new();

        public PostMergeProcessingStats GetAndResetStats()
        {
            _stats.QueueLength = _processing.InputCount;
            return Interlocked.Exchange(ref _stats, new());
        }

        public Compressor(LogIndexStorage storage, ILogger logger, int compressionDistance, int parallelism)
        {
            _storage = storage;
            _logger = logger;

            MinLengthToCompress = compressionDistance * BlockNumSize;

            if (parallelism < 1) throw new ArgumentException("Compression parallelism degree must be a positive value.", nameof(parallelism));
            _processing = new(x => CompressValue(x.Item1, x.Item2), new() { MaxDegreeOfParallelism = parallelism, BoundedCapacity = 10_000 });
        }

        public bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue)
        {
            if (dbValue.Length < MinLengthToCompress)
                return false;

            var dbKeyArr = dbKey.ToArray();
            if (!_compressQueue.TryAdd(dbKeyArr, true))
                return false;

            if (_processing.Post((topicIndex, dbKeyArr)))
                return true;

            _compressQueue.TryRemove(dbKeyArr, out _);
            return false;
        }

        public async Task EnqueueAsync(int? topicIndex, byte[] dbKey)
        {
            await _processing.SendAsync((topicIndex, dbKey));
            _queueEmptyEvent.Reset();
        }

        public Task WaitUntilEmptyAsync(TimeSpan waitTime, CancellationToken cancellationToken) =>
            _queueEmptyEvent.WaitHandle.WaitOneAsync(waitTime, cancellationToken);

        private void CompressValue(int? topicIndex, byte[] dbKey)
        {
            try
            {
                if (_cts.IsCancellationRequested)
                    return;

                _startEvent.Wait(_cts.Token);

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
                Span<byte> dbKeyComp = stackalloc byte[key.Length + BlockNumSize];
                key.CopyTo(dbKeyComp);
                SetKeyBlockNum(dbKeyComp[key.Length..], postfixBlock);

                timestamp = Stopwatch.GetTimestamp();
                dbValue = _storage.CompressDbValue(dbValue);
                _stats.CompressingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                // Put compressed value at a new key and clear the uncompressed one
                timestamp = Stopwatch.GetTimestamp();
                using (IWriteBatch batch = db.StartWriteBatch())
                {
                    Span<byte> truncateOp = MergeOps.Create(
                        MergeOp.TruncateOp, truncateBlock, stackalloc byte[MergeOps.Size]
                    );

                    batch.PutSpan(dbKeyComp, dbValue);
                    batch.Merge(dbKey, truncateOp);
                }

                _stats.PuttingValues.Include(Stopwatch.GetElapsedTime(timestamp));

                if (topicIndex is null)
                    Interlocked.Increment(ref _stats.CompressedAddressKeys);
                else
                    Interlocked.Increment(ref _stats.CompressedTopicKeys);

                _stats.Execution.Include(Stopwatch.GetElapsedTime(execTimestamp));
            }
            catch (Exception ex)
            {
                if (_logger.IsError)
                    _logger.Error("Error during post-merge compression.", ex);

                _storage.OnError(ex);
                _cts.Cancel();
            }
            finally
            {
                _compressQueue.TryRemove(dbKey, out _);

                if (_processing.InputCount == 0) // TODO: take processing items into account
                    _queueEmptyEvent.Set();
            }
        }

        public void Start() => _startEvent.Set();

        public Task StopAsync()
        {
            _cts.Cancel();
            _processing.Complete();
            return _processing.Completion;
        }

        public void Dispose()
        {
            _startEvent.Dispose();
            _queueEmptyEvent.Dispose();
            _cts.Dispose();
        }
    }

    public sealed class NoOpCompressor : ICompressor
    {
        public int MinLengthToCompress => 256;
        public PostMergeProcessingStats Stats { get; } = new();
        public PostMergeProcessingStats GetAndResetStats() => Stats;
        public bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue) => false;
        public Task EnqueueAsync(int? topicIndex, byte[] dbKey) => Task.CompletedTask;
        public Task WaitUntilEmptyAsync(TimeSpan waitTime, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
