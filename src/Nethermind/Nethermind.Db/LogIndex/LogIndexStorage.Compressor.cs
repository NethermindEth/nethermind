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

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage
{
    /// <summary>
    /// Does background compression for keys with the number of blocks above the threshold.
    /// </summary>
    /// <remarks>
    /// Consumes "transient" keys with value being too big (see <see cref="ILogIndexConfig.CompressionDistance"/>)
    /// from <see cref="MergeOperator"/> and performs compression in the background. <br/>
    /// Can utilize multiple threads, as per <see cref="ILogIndexConfig.MaxCompressionParallelism"/>.
    ///
    /// <para>
    /// For each "transient" key in the queue performs the following:
    /// <list type="number">
    /// <item> reads the latest (uncompressed) value from the database; </item>
    /// <item> truncates potentially reorgable blocks from the sequence (as compressed values become immutable); </item>
    /// <item> reverts sequence if needed - so the new value is always in ascending order; </item>
    /// <item> compresses the sequence using specified TurboPFor <see cref="CompressionAlgorithm"/>; </item>
    /// <item> stores the compressed value at a new pair using <c>{address-or-topic} || ({first-block-number-in-the-sequence} + 1)</c> as a key; </item>
    /// <item> queues truncation for the "transient" key via <see cref="MergeOp.Truncate"/> - to remove finalized blocks from the old sequence. </item>
    /// </list>
    /// </para>
    /// Last 2 operations are done via a single <see cref="IWriteBatch"/> to maintain data consistency.
    /// </remarks>
    internal class Compressor : ICompressor
    {
        private readonly int _minLengthToCompress;

        // Used instead of a channel to prevent duplicates
        private readonly ConcurrentDictionary<byte[], bool> _compressQueue = new(Bytes.EqualityComparer);
        private readonly ConcurrentDictionary<byte[], bool>.AlternateLookup<ReadOnlySpan<byte>> _compressQueueLookup;
        private readonly LogIndexStorage _storage;
        private readonly ActionBlock<(int?, byte[])> _processing;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _queueLock = new();
        private TaskCompletionSource? _queueEmpty;

        private int _pendingCount;
        private PostMergeProcessingStats _stats = new();

        public PostMergeProcessingStats GetAndResetStats()
        {
            _stats.QueueLength = _processing.InputCount;
            return Interlocked.Exchange(ref _stats, new());
        }

        public Compressor(LogIndexStorage storage, int compressionDistance, int parallelism)
        {
            _compressQueueLookup = _compressQueue.GetAlternateLookup<ReadOnlySpan<byte>>();
            _storage = storage;

            _minLengthToCompress = compressionDistance * BlockNumberSize;

            if (parallelism < 1) throw new ArgumentException("Compression parallelism degree must be a positive value.", nameof(parallelism));
            _processing = new(x =>
            {
                if (!_started.Task.IsCompletedSuccessfully) return CompressAfterStartAsync(x.Item1, x.Item2);
                CompressValue(x.Item1, x.Item2);
                return Task.CompletedTask;
            }, new() { MaxDegreeOfParallelism = parallelism, BoundedCapacity = 10_000 });
        }

        private async Task CompressAfterStartAsync(int? topicIndex, byte[] dbKey)
        {
            await _started.Task.ConfigureAwait(false);
            CompressValue(topicIndex, dbKey);
        }

        public bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue)
        {
            if (dbValue.Length < _minLengthToCompress)
                return false;

            if (_compressQueueLookup.TryGetValue(dbKey, out _))
                return false;

            byte[] dbKeyArr = dbKey.ToArray();
            if (!_compressQueue.TryAdd(dbKeyArr, true))
                return false;

            AddPending();
            if (_processing.Post((topicIndex, dbKeyArr)))
                return true;

            CompletePending();
            _compressQueue.TryRemove(dbKeyArr, out _);
            return false;
        }

        public async Task EnqueueAsync(int? topicIndex, byte[] dbKey)
        {
            AddPending();
            bool accepted = false;
            try
            {
                accepted = await _processing.SendAsync((topicIndex, dbKey)).ConfigureAwait(false);
            }
            finally
            {
                if (!accepted) CompletePending();
            }
        }

        public async Task WaitUntilEmptyAsync(TimeSpan waitTime, CancellationToken cancellationToken)
        {
            Task empty;
            lock (_queueLock)
            {
                empty = Volatile.Read(ref _pendingCount) == 0
                    ? Task.CompletedTask
                    : (_queueEmpty ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            try
            {
                await empty.WaitAsync(waitTime, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Compaction may continue after its bounded wait for compression expires.
            }
        }

        private void AddPending() => Interlocked.Increment(ref _pendingCount);

        private void CompletePending()
        {
            if (Interlocked.Decrement(ref _pendingCount) != 0) return;

            lock (_queueLock)
            {
                if (Volatile.Read(ref _pendingCount) != 0) return;

                _queueEmpty?.TrySetResult();
                _queueEmpty = null;
            }
        }

        private void CompressValue(int? topicIndex, byte[] dbKey)
        {
            try
            {
                if (_storage.HasBackgroundError)
                    return;

                long execTimestamp = Stopwatch.GetTimestamp();
                IDb db = _storage.GetDb(topicIndex);

                long timestamp = Stopwatch.GetTimestamp();
                Span<byte> dbValue = db.Get(dbKey);
                _stats.DBReading.Include(Stopwatch.GetElapsedTime(timestamp));

                // Do not compress blocks that can be reorged, as compressed data is immutable
                if (!UseBackwardSyncFor(dbKey))
                    dbValue = _storage.RemoveReorgableBlocks(dbValue);

                if (dbValue.Length < _minLengthToCompress)
                    return;

                int truncateBlock = ReadLastBlockNumber(dbValue);

                ReverseBlocksIfNeeded(dbValue);

                int postfixBlock = ReadBlockNumber(dbValue);

                ReadOnlySpan<byte> key = ExtractKey(dbKey);
                Span<byte> dbKeyComp = stackalloc byte[key.Length + BlockNumberSize];
                key.CopyTo(dbKeyComp);
                WriteKeyBlockNumber(dbKeyComp[key.Length..], postfixBlock);

                timestamp = Stopwatch.GetTimestamp();
                dbValue = _storage.CompressDbValue(dbKey, dbValue);
                _stats.CompressingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                // Put compressed value at a new key and clear the uncompressed one
                timestamp = Stopwatch.GetTimestamp();
                using (IWriteBatch batch = db.StartWriteBatch())
                {
                    Span<byte> truncateOp = MergeOps.Create(MergeOp.Truncate, truncateBlock, stackalloc byte[MergeOps.Size]);
                    batch.PutSpan(dbKeyComp, dbValue);
                    batch.Merge(dbKey, truncateOp);
                }

                _stats.DBSaving.Include(Stopwatch.GetElapsedTime(timestamp));

                Interlocked.Increment(ref topicIndex is null ? ref _stats.CompressedAddressKeys : ref _stats.CompressedTopicKeys);
                _stats.Total.Include(Stopwatch.GetElapsedTime(execTimestamp));
            }
            catch (Exception ex)
            {
                _storage.OnBackgroundError<Compressor>(ex);
            }
            finally
            {
                _compressQueue.TryRemove(dbKey, out _);

                CompletePending();
            }
        }

        public void Start() => _started.TrySetResult();

        public Task StopAsync()
        {
            _processing.Complete();
            return _processing.Completion; // Wait for the compression queue to finish
        }

        public void Dispose() { }
    }

    public sealed class NoOpCompressor : ICompressor
    {
        private PostMergeProcessingStats Stats { get; } = new();
        public PostMergeProcessingStats GetAndResetStats() => Stats;
        public bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue) => false;
        public Task EnqueueAsync(int? topicIndex, byte[] dbKey) => Task.CompletedTask;
        public Task WaitUntilEmptyAsync(TimeSpan waitTime, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Start() { }
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    public interface ICompressor : IDisposable
    {
        PostMergeProcessingStats GetAndResetStats();

        bool TryEnqueue(int? topicIndex, ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> dbValue);
        Task EnqueueAsync(int? topicIndex, byte[] dbKey);
        Task WaitUntilEmptyAsync(TimeSpan waitTime = default, CancellationToken cancellationToken = default);

        void Start();
        Task StopAsync();
    }
}
