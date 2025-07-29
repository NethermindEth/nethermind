// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db;

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

        private CompactingStats _stats = new();
        private readonly LogIndexStorage _storage;
        private readonly ILogger _logger;
        private readonly int _compactionDistance;

        // TODO: simplify concurrency handling
        private readonly AutoResetEvent _runOnceEvent = new(false);
        private readonly CancellationTokenSource _cancellationSource = new();
        private readonly ManualResetEvent _compactionStartedEvent = new(false);
        private readonly ManualResetEvent _compactionEndedEvent = new(true);
        private readonly Task _compactionTask;

        public Compactor(LogIndexStorage storage, ILogger logger, int compactionDistance)
        {
            _storage = storage;
            _logger = logger;

            if (compactionDistance < 1) throw new ArgumentException("Compaction distance must be a positive value.", nameof(compactionDistance));
            _compactionDistance = compactionDistance;

            _lastAtMin = storage.GetMinBlockNumber();
            _lastAtMax = storage.GetMaxBlockNumber();

            _compactionTask = DoCompactAsync();
        }

        public CompactingStats GetAndResetStats() => Interlocked.Exchange(ref _stats, new());

        // Not thread-safe
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
            await _compactionEndedEvent.WaitOneAsync(CancellationToken.None);
        }

        public async Task<CompactingStats> ForceAsync()
        {
            // Wait for the previous one to finish
            await _compactionEndedEvent.WaitOneAsync(_cancellationSource.Token);

            _runOnceEvent.Set();
            await _compactionStartedEvent.WaitOneAsync(100, _cancellationSource.Token);
            await _compactionEndedEvent.WaitOneAsync(_cancellationSource.Token);
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

                    _compactionEndedEvent.Reset();
                    _compactionStartedEvent.Set();

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
                }
                catch (TaskCanceledException ex) when (ex.CancellationToken == cancellation)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (_logger.IsError)
                        _logger.Error("Failed to compact log index", ex);
                }
                finally
                {
                    _compactionStartedEvent.Reset();
                    _compactionEndedEvent.Set();
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
