// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingWork : IDisposable
{
    private readonly StreamingConnection _connection;
    private readonly BackendAdmission.BackendLease _slot;
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationTokenSource _deadline;
    private readonly Task<StreamingResult> _worker;
    private int _disposed;

    internal SessionOperation Operation { get; }
    internal Task<StreamingResult> Response { get; }

    internal StreamingWork(StreamingConnection connection, SessionOperation operation, BackendAdmission.BackendLease slot,
        Func<SessionOperation, CancellationToken, Task<StreamingResult>> execute, TimeSpan timeout, TimeProvider time, CancellationToken cancellation, CancellationTokenSource? deadline = null)
    {
        _connection = connection;
        Operation = operation;
        _slot = slot;
        _deadline = deadline ?? new CancellationTokenSource(timeout, time);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _deadline.Token);
        try { _worker = execute(operation, _cancellation.Token); }
        catch (Exception exception) { _worker = Task.FromException<StreamingResult>(exception); }
        Response = _worker.WaitAsync(_cancellation.Token);
    }

    internal bool DeadlineExceeded => _deadline.IsCancellationRequested;

    internal void CheckCancellation() => _cancellation.Token.ThrowIfCancellationRequested();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = Response.ContinueWith(static failed => _ = failed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { _cancellation.Cancel(); }
        finally
        {
            _ = _slot.ReleaseAfter(_worker, () =>
            {
                try { _connection.FinishOperation(Operation); }
                finally { _cancellation.Dispose(); _deadline.Dispose(); }
            });
        }
    }
}
