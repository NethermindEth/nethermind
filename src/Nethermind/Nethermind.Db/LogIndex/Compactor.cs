// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db;

// Not thread-safe
partial class LogIndexStorage
{
    private interface ICompactor
    {
        CompactingStats GetAndResetStats();
        bool TryEnqueue();
        Task StopAsync();
        Task<CompactingStats> ForceAsync();
    }

    private class Compactor : ICompactor
    {
        private int? _lastAtMin;
        private int? _lastAtMax;

        // TODO: simplify concurrency handling
        private readonly AutoResetEvent _runOnceEvent = new(false);
        private readonly Task _compactionTask;
        private readonly CancellationTokenSource _cancellationSource = new();
        private TaskCompletionSource _completedOnceSource = new();

        private CompactingStats _stats = new();
        private readonly LogIndexStorage _storage;
        private readonly ILogger _logger;
        private readonly int _compactionDistance;

        public Compactor(LogIndexStorage storage, ILogger logger, int compactionDistance)
        {
            _storage = storage;
            _logger = logger;

            if (compactionDistance < 1) throw new ArgumentException("Compaction distance must be a positive value.", nameof(compactionDistance));
            _compactionDistance = compactionDistance;

            _lastAtMin = storage.GetMinBlockNumber();
            _lastAtMax = storage.GetMaxBlockNumber();
            _compactionTask = Task.CompletedTask;
            _compactionTask = DoCompactAsync();
            _completedOnceSource.SetResult();
        }

        public CompactingStats GetAndResetStats() => Interlocked.Exchange(ref _stats, new());

        public bool TryEnqueue()
        {
            _lastAtMin ??= _storage.GetMinBlockNumber();
            _lastAtMax ??= _storage.GetMaxBlockNumber();

            var uncompacted = 0;
            if (_storage.GetMinBlockNumber() is { } storageMin && storageMin < _lastAtMin)
                uncompacted += _lastAtMin.Value - storageMin;
            if (_storage.GetMaxBlockNumber() is { } storageMax && storageMax > _lastAtMax)
                uncompacted += storageMax - _lastAtMax.Value;

            // TODO: cover other cases - space usage, RocksDB stats?
            if (uncompacted < _compactionDistance)
                return false;

            if (!_runOnceEvent.Set())
                return false;

            _lastAtMin = _storage.GetMinBlockNumber();
            _lastAtMax = _storage.GetMaxBlockNumber();
            return true;
        }

        public async Task StopAsync()
        {
            await _cancellationSource.CancelAsync();
            await _compactionTask;
        }

        public async Task<CompactingStats> ForceAsync()
        {
            await _completedOnceSource.Task;
            _runOnceEvent.Set();
            await _completedOnceSource.Task; // TODO: handle race condition here
            return _stats;
        }

        private async Task DoCompactAsync()
        {
            CancellationToken cancellation = _cancellationSource.Token;
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await _runOnceEvent.WaitOneAsync(cancellation);
                    _completedOnceSource = new();

                    if (_logger.IsTrace)
                        _logger.Trace("Compacting log index");

                    var addrTimestamp = Stopwatch.GetTimestamp();
                    _storage._addressDb.Compact();
                    _stats.Addresses.Include(Stopwatch.GetElapsedTime(addrTimestamp));

                    if (cancellation.IsCancellationRequested)
                        return;

                    var topicTimestamp = Stopwatch.GetTimestamp();
                    _storage._topicsDb.Compact();
                    _stats.Topics.Include(Stopwatch.GetElapsedTime(topicTimestamp));

                    TimeSpan total = Stopwatch.GetElapsedTime(addrTimestamp) + Stopwatch.GetElapsedTime(topicTimestamp);
                    _stats.Total.Include(total);

                    if (_logger.IsTrace)
                        _logger.Trace($"Compacted log index in {total}");

                    _completedOnceSource.SetResult();
                }
                catch (TaskCanceledException ex) when (ex.CancellationToken == cancellation)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (_logger.IsError)
                        _logger.Error("Failed to compact log index", ex);

                    _completedOnceSource.SetException(ex);
                }
            }
        }
    }

    private class NoOpCompactor : ICompactor
    {
        public CompactingStats GetAndResetStats() => new();
        public bool TryEnqueue() => false;
        public Task StopAsync() => Task.CompletedTask;
        public Task<CompactingStats> ForceAsync() => Task.FromResult(new CompactingStats());
    }
}
