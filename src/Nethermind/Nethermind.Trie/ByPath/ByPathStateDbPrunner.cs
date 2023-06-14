// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Trie.ByPath;
public class ByPathStateDbPrunner
{
    private Task? _pruneTask;
    private CancellationTokenSource _pruningTaskCancellationTokenSource = new();
    private IKeyValueStoreWithBatching _keyValueStore;
    private BlockingCollection<KeyRange> _cleanupQueue;

    public ByPathStateDbPrunner(IKeyValueStoreWithBatching keyValueStore)
    {
        _keyValueStore = keyValueStore;
        //_cleanupQueue = new BlockingCollection<KeyRange>();
        //_pruneTask = BuildCleaningTask();
        //_pruneTask.Start();
    }

    public void EnqueueRange(Span<byte> from, Span<byte> to)
    {
        _cleanupQueue.Add(new KeyRange(from, to));
        Console.WriteLine("Adding range - queue size {0}", _cleanupQueue.Count);
    }

    public void EndOfCleanupRequests()
    {
        Console.WriteLine("Complete Adding  - queue size {0}", _cleanupQueue.Count);
        _cleanupQueue.CompleteAdding();
    }

    public void Start()
    {
        if (_pruneTask?.IsCompleted == false)
        {
            Console.WriteLine("Cancel queue - {0}", _cleanupQueue.Count);
            _pruningTaskCancellationTokenSource.Cancel();
            _pruneTask.Wait();
        }
        if (_pruneTask?.IsCompleted == true)
        {
            _pruneTask = null;
        }

        if (_pruneTask is null)
        {
            Console.WriteLine("New queue - {0}", _cleanupQueue?.Count ?? 0);
            _cleanupQueue = new BlockingCollection<KeyRange>();
            _pruningTaskCancellationTokenSource = new();
            _pruneTask = BuildCleaningTask();
            _pruneTask.Start();
        }
    }

    public bool IsPruningComplete => _pruneTask is null || _pruneTask.IsCompleted;

    public void Wait()
    {
        Console.WriteLine("Waiting");
        _pruneTask.Wait();
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
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine("InvalidOperationException {0}", ex);
                }
                catch (OperationCanceledException cancelledEx)
                {
                    Console.WriteLine("OperationCanceledException {0}", cancelledEx);
                }

                if (toBeRemoved is not null)
                {
                    _keyValueStore.DeleteByRange(toBeRemoved.From, toBeRemoved.To);
                }
            }
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
