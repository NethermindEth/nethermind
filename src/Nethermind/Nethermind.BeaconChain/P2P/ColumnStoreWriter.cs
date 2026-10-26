// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Runs the store writes of <see cref="DataColumnSidecarPool"/> in arrival order on one dedicated thread.</summary>
/// <remarks>
/// Gossip validation runs inside the pubsub router's lock, which every peer's read loop takes on a thread-pool thread, so a store write
/// or an epoch's prune there stalls the messages of all peers and parks a pool thread per peer on that lock.
/// </remarks>
public sealed class ColumnStoreWriter : IDisposable
{
    /// <summary>The most writes queued; a write past it waits on the caller for room, so a backlog slows its producer instead of growing.</summary>
    internal const int MaxQueued = 4096;

    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _writes = new(MaxQueued);
    private readonly Thread _thread;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ColumnStoreWriter(ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<ColumnStoreWriter>();
        _thread = new Thread(Run) { IsBackground = true, Name = "Beacon column store writer" };
        _thread.Start();
    }

    /// <summary>Queues <paramref name="write"/> behind every earlier one.</summary>
    /// <remarks>
    /// After <see cref="Dispose"/> the write is dropped, as the store is closing: running it then could overtake a queued one or outlive the store.
    /// A column lost this way is found by the start-up check of the stored range, which keeps its slot from being served as complete.
    /// </remarks>
    public void Post(Action write)
    {
        try
        {
            _writes.Add(write);
        }
        catch (InvalidOperationException)
        {
            if (_logger.IsDebug) _logger.Debug("Data column sidecar store write dropped at shutdown");
        }
    }

    /// <summary>Completes once every write posted before this call has run; after <see cref="Dispose"/> has started, once the writer has drained the queue.</summary>
    /// <remarks>Waits on the caller for room when the queue is full, as <see cref="Post"/> does.</remarks>
    public Task WhenWritten()
    {
        TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _writes.Add(written.SetResult);
            return written.Task;
        }
        catch (InvalidOperationException)
        {
            return _drained.Task;
        }
    }

    private void Run()
    {
        try
        {
            foreach (Action write in _writes.GetConsumingEnumerable())
            {
                try
                {
                    write();
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Data column sidecar store write failed: {e.Message}");
                }
            }
        }
        finally
        {
            _drained.TrySetResult();
        }
    }

    /// <summary>Stops taking writes and waits for the queued ones to finish, so none runs against a closed store.</summary>
    public void Dispose()
    {
        _writes.CompleteAdding();
        _thread.Join();
    }
}
