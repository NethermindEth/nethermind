// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage
{
    /// <summary>
    /// Periodically forces background log index compaction for every N added blocks.
    /// </summary>
    private class Compactor : ICompactor
    {
        private int? _lastAtMin;
        private int? _lastAtMax;

        private CompactingStats _stats = new();
        private readonly LogIndexStorage _storage;
        private readonly ILogger _logger;
        private readonly int _compactionDistance;

        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<TaskCompletionSource?> _channel = Channel.CreateBounded<TaskCompletionSource?>(1);
        private volatile TaskCompletionSource? _pendingForce;
        private readonly Task _compactionTask;

        public Compactor(LogIndexStorage storage, ILogger logger, int compactionDistance)
        {
            _storage = storage;
            _logger = logger;

            if (compactionDistance < 1) throw new ArgumentException("Compaction distance must be a positive value.", nameof(compactionDistance));
            _compactionDistance = compactionDistance;

            _lastAtMin = storage.MinBlockNumber;
            _lastAtMax = storage.MaxBlockNumber;

            _compactionTask = DoCompactAsync();
        }

        public CompactingStats GetAndResetStats() => Interlocked.Exchange(ref _stats, new());

        // Not thread-safe
        public bool TryEnqueue()
        {
            if (_cts.IsCancellationRequested)
                return false;

            _lastAtMin ??= _storage.MinBlockNumber;
            _lastAtMax ??= _storage.MaxBlockNumber;

            var uncompacted = 0;
            if (_storage.MinBlockNumber is { } storageMin && storageMin < _lastAtMin)
                uncompacted += _lastAtMin.Value - storageMin;
            if (_storage.MaxBlockNumber is { } storageMax && storageMax > _lastAtMax)
                uncompacted += storageMax - _lastAtMax.Value;

            if (uncompacted < _compactionDistance)
                return false;

            if (!_channel.Writer.TryWrite(null))
                return false;

            _lastAtMin = _storage.MinBlockNumber;
            _lastAtMax = _storage.MaxBlockNumber;
            return true;
        }

        public async Task StopAsync()
        {
            await _cts.CancelAsync();
            _channel.Writer.TryComplete();
            await _compactionTask;
        }

        public async Task<CompactingStats> ForceAsync()
        {
            // Coalesce concurrent calls — all callers share a single compaction
            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource? existing = Interlocked.CompareExchange(ref _pendingForce, tcs, null);
            if (existing is not null)
            {
                await existing.Task;
                return _stats;
            }

            try
            {
                await _channel.Writer.WriteAsync(tcs, _cts.Token);
            }
            catch
            {
                Interlocked.CompareExchange(ref _pendingForce, null, tcs);
                tcs.TrySetCanceled();
                throw;
            }

            await tcs.Task;
            return _stats;
        }

        private async Task DoCompactAsync()
        {
            CancellationToken cancellation = _cts.Token;
            try
            {
                await foreach (TaskCompletionSource? tcs in _channel.Reader.ReadAllAsync(cancellation))
                {
                    try
                    {
                        if (_logger.IsInfo) _logger.Info($"Log index: compaction started, DB size: {_storage.GetDbSize()}");

                        var timestamp = Stopwatch.GetTimestamp();
                        _storage._rootDb.Compact();

                        TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);
                        _stats.Total.Include(elapsed);

                        if (_logger.IsInfo) _logger.Info($"Log index: compaction ended in {elapsed}, DB size: {_storage.GetDbSize()}");

                        tcs?.TrySetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        tcs?.TrySetCanceled();
                    }
                    catch (Exception ex)
                    {
                        tcs?.TrySetException(ex);
                        _storage.OnBackgroundError<Compactor>(ex);
                        await _cts.CancelAsync();
                        _channel.Writer.TryComplete();
                    }
                    finally
                    {
                        if (tcs is not null) Interlocked.CompareExchange(ref _pendingForce, null, tcs);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsDebug) _logger.Debug("Log index: compaction loop canceled.");
            }
            finally
            {
                while (_channel.Reader.TryRead(out TaskCompletionSource? remaining))
                {
                    remaining?.TrySetCanceled();
                    if (remaining is not null) Interlocked.CompareExchange(ref _pendingForce, null, remaining);
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

    private interface ICompactor
    {
        CompactingStats GetAndResetStats();
        bool TryEnqueue();
        Task StopAsync();
        Task<CompactingStats> ForceAsync();
    }
}
