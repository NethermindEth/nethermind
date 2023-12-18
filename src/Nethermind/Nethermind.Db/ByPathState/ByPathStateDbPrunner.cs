// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db.ByPathState;
public class ByPathStateDbPrunner
{
    private StateColumns _column;
    private Task? _pruneTask;
    private CancellationTokenSource _pruningTaskCancellationTokenSource = new();
    private IKeyValueStoreWithBatching _keyValueStore;
    private BlockingCollection<KeyRange>? _cleanupQueue;
    private readonly ILogger _logger;
    private int _totalCleanupRequests;
    private readonly System.Timers.Timer _logTimer;
    private DateTime _cleanUpStart;

    public ByPathStateDbPrunner(StateColumns column, IKeyValueStoreWithBatching keyValueStore, ILogManager? logManager)
    {
        _column = column;
        _keyValueStore = keyValueStore;
        _logTimer = new System.Timers.Timer(TimeSpan.FromSeconds(3));
        _logTimer.Elapsed += _logTimer_Elapsed;
        _logger = logManager?.GetClassLogger<ByPathStateDbPrunner>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public void EnqueueRange(Span<byte> from, Span<byte> to)
    {
        if (_cleanupQueue is null)
        {
            if (_logger.IsInfo) _logger.Info($"{_column} -  Cleanup queue not available");
            return;
        }
        if (_logger.IsTrace) _logger.Trace($"Enqueueing range for deletions - from: {from.ToHexString()} | to: {to.ToHexString()}");
        _cleanupQueue.Add(new KeyRange(from, to));
    }

    public void EndOfCleanupRequests()
    {
        if (_cleanupQueue is null)
        {
            if (_logger.IsInfo) _logger.Info($"{_column} - Cleanup queue not available");
            return;
        }
        if (_logger.IsInfo) _logger.Info($"{_column} - Cleanup requests completed - queue size {_cleanupQueue.Count}");
        _cleanupQueue.CompleteAdding();
    }

    public void Start()
    {
        if (_pruneTask?.IsCompleted == false)
        {
            if (_logger.IsWarn) _logger.Warn($"{_column} - Cleanup task not finished - cancelling - queue size {_cleanupQueue.Count}");
            _pruningTaskCancellationTokenSource.Cancel();
            _pruneTask.Wait();
        }
        if (_pruneTask?.IsCompleted == true)
        {
            _pruneTask = null;
        }

        if (_pruneTask is null)
        {
            if (_logger.IsWarn) _logger.Warn($"{_column} - Staring new db cleanup task");
            _cleanupQueue = new BlockingCollection<KeyRange>();
            _pruningTaskCancellationTokenSource = new();
            _pruneTask = BuildCleaningTask();
            _pruneTask.Start();
            _cleanUpStart = DateTime.Now;
            Interlocked.Exchange(ref _totalCleanupRequests, 0);
            _logTimer.Start();
        }
    }

    public bool IsPruningComplete => _pruneTask is null || _pruneTask.IsCompleted;

    public void Wait()
    {
        if (_pruneTask is not null)
        {
            if (_logger.IsInfo) _logger.Info($"{_column} - Waiting for cleanup task to finish");
            _pruneTask.Wait();
        }
    }

    private void _logTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        TimeSpan sinceStart = DateTime.Now - _cleanUpStart;
        int processed = (int)(_totalCleanupRequests / sinceStart.TotalSeconds);
        if (_logger.IsInfo) _logger.Info($"Processing db deletion for {_column} at {processed} requests / s");
    }

    private Task BuildCleaningTask()
    {
        return new Task(() =>
        {
            while (!_cleanupQueue.IsCompleted)
            {
                KeyRange toBeRemoved = null;
                try
                {
                    toBeRemoved = _cleanupQueue.Take(_pruningTaskCancellationTokenSource.Token);
                }
                catch (InvalidOperationException) { }
                catch (OperationCanceledException)
                {
                    if (_logger.IsWarn) _logger.Warn($"{_column} - Cleanup task cancelled");
                    break;
                }

                if (toBeRemoved is null) continue;

                _keyValueStore.DeleteByRange(toBeRemoved.From, toBeRemoved.To);
                Interlocked.Increment(ref _totalCleanupRequests);
            }
            if (_logger.IsInfo) _logger.Info($"Processed {_column} db deletion - total {_totalCleanupRequests} at {(int)(_totalCleanupRequests / (DateTime.Now - _cleanUpStart).TotalSeconds)} requests / s");
            _logTimer.Stop();
        });
    }


    private class KeyRange
    {
        public byte[] From;
        public byte[] To;

        public KeyRange(Span<byte> from, Span<byte> to)
        {
            From = from.ToArray();
            To = to.ToArray();
        }
    }
}
