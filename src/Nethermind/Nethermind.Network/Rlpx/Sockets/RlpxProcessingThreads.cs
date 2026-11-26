// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx.Sockets;

/// <summary>
/// Dedicated threads that handle what RLPx connections receive, each connection on one of them.
/// </summary>
/// <remarks>
/// As the DotNetty executor group of <see cref="RlpxHost"/>, each thread has one FIFO shared by its connections, so a
/// connection's items run in order and, with a single thread, every peer's messages are handled one at a time.
/// </remarks>
internal sealed class RlpxProcessingThreads
{
    private readonly ProcessingThread[] _threads;
    private int _next = -1;

    /// <param name="count">The number of threads; values of 1 or less mean one, as for the DotNetty executor group.</param>
    /// <param name="logger">Logs an item that fails unexpectedly.</param>
    public RlpxProcessingThreads(int count, ILogger logger)
    {
        _threads = new ProcessingThread[Math.Max(1, count)];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new ProcessingThread(i, logger);
        }
    }

    /// <summary>Picks the thread for a new connection, in turn.</summary>
    public ProcessingThread Next() => _threads[(uint)Interlocked.Increment(ref _next) % (uint)_threads.Length];

    /// <summary>Stops taking items, lets every thread finish the items it has, and completes when all have exited.</summary>
    public Task StopAsync()
    {
        Task[] stopped = new Task[_threads.Length];
        for (int i = 0; i < _threads.Length; i++)
        {
            stopped[i] = _threads[i].StopAsync();
        }

        return Task.WhenAll(stopped);
    }

    internal sealed class ProcessingThread
    {
        private readonly Channel<ProcessingItem> _queue = Channel.CreateUnbounded<ProcessingItem>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ILogger _logger;
        private readonly Thread _thread;

        public ProcessingThread(int index, ILogger logger)
        {
            _logger = logger;
            _thread = new Thread(Run) { IsBackground = true, Name = $"RLPx processing {index}" };
            _thread.Start();
        }

        /// <summary>Queues an item behind those already queued.</summary>
        /// <returns><see langword="false"/> once the thread has stopped taking items: the caller still owns the item.</returns>
        public bool TryEnqueue(in ProcessingItem item) => _queue.Writer.TryWrite(item);

        public Task StopAsync()
        {
            _queue.Writer.TryComplete();
            return _exited.Task;
        }

        private void Run()
        {
            ChannelReader<ProcessingItem> reader = _queue.Reader;
            try
            {
                while (true)
                {
                    while (reader.TryRead(out ProcessingItem item))
                    {
                        try
                        {
                            item.Connection.Process(in item);
                        }
                        catch (Exception e)
                        {
                            if (_logger.IsError) _logger.Error($"Unexpected failure while handling a received packet on {_thread.Name}", e);
                        }
                    }

                    ValueTask<bool> waiting = reader.WaitToReadAsync();
                    if (!(waiting.IsCompletedSuccessfully ? waiting.Result : waiting.AsTask().GetAwaiter().GetResult()))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _exited.TrySetResult();
            }
        }
    }
}

/// <summary>What a connection hands its processing thread: a received packet, an error to classify, or the final cleanup.</summary>
internal readonly struct ProcessingItem(SocketRlpxConnection connection, PooledPacket? packet, Exception? error)
{
    public SocketRlpxConnection Connection { get; } = connection;

    public PooledPacket? Packet { get; } = packet;

    public Exception? Error { get; } = error;
}
