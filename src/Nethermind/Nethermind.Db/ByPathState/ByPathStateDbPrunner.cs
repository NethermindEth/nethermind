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
    private Task? _pruneTask;
    private CancellationTokenSource _pruningTaskCancellationTokenSource = new();
    private IKeyValueStoreWithBatching _keyValueStore;
    private BlockingCollection<KeyRange> _cleanupQueue;
    private readonly ILogger _logger;

    public ByPathStateDbPrunner(IKeyValueStoreWithBatching keyValueStore, ILogManager? logManager)
    {
        _keyValueStore = keyValueStore;
        _logger = logManager?.GetClassLogger<ByPathStateDbPrunner>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public void EnqueueRange(Span<byte> from, Span<byte> to)
    {
        if (_cleanupQueue is null)
        {
            if (_logger.IsInfo) _logger.Info("Cleanup queue not available");
            return;
        }
        if (_logger.IsDebug) _logger.Debug($"Enqueueing range for deletions - from: {from.ToHexString()} | to: {to.ToHexString()}");
        _cleanupQueue.Add(new KeyRange(from, to));
    }

    public void EndOfCleanupRequests()
    {
        if (_cleanupQueue is null)
        {
            if (_logger.IsInfo) _logger.Info("Cleanup queue not available");
            return;
        }
        if (_logger.IsInfo) _logger.Info($"Cleanup requests completed - queue size {_cleanupQueue.Count}");
        _cleanupQueue.CompleteAdding();
    }

    public void Start()
    {
        if (_pruneTask?.IsCompleted == false)
        {
            if (_logger.IsWarn) _logger.Warn($"Cleanup task not finished - cancelling - queue size {_cleanupQueue.Count}");
            _pruningTaskCancellationTokenSource.Cancel();
            _pruneTask.Wait();
        }
        if (_pruneTask?.IsCompleted == true)
        {
            _pruneTask = null;
        }

        if (_pruneTask is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Staring new db cleanup task");
            _cleanupQueue = new BlockingCollection<KeyRange>();
            _pruningTaskCancellationTokenSource = new();
            _pruneTask = BuildCleaningTask();
            _pruneTask.Start();
        }
    }

    public bool IsPruningComplete => _pruneTask is null || _pruneTask.IsCompleted;

    public void Wait()
    {
        if (_pruneTask is not null)
        {
            if (_logger.IsInfo) _logger.Info($"Waiting for cleanup task to finish");
            _pruneTask.Wait();
        }
    }

    private Task BuildCleaningTask()
    {
        return new Task(() =>
        {
            int removed = 0;
            Stopwatch sw = new();
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
                    if (_logger.IsWarn) _logger.Warn($"Cleanup taks cancelled");
                    break;
                }

                if (toBeRemoved is not null)
                {
                    _keyValueStore.DeleteByRange(toBeRemoved.From, toBeRemoved.To);
                    Interlocked.Increment(ref removed);
                    if (removed > 10_000)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Executed 10 000 deletions in {sw.ElapsedMilliseconds} ms");
                        sw.Restart();
                        Interlocked.Exchange(ref removed, 0);
                    }
                }
            }
            sw.Stop();
            if (_logger.IsWarn) _logger.Warn($"Executed {removed} deletions in {sw.ElapsedMilliseconds} ms");
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
