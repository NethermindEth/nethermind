// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

/// <summary>Serializes sends and prevents reuse of a connection after an incomplete message.</summary>
internal sealed class SocketSendLock(long maxUnsentBytes = 0, int sendTimeout = 0) : IDisposable
{
    private const long MinSendBytesPerSecond = 64 * 1024;
    private const long MaxSendExtensionMilliseconds = 5 * 60 * 1000;

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _faulted;
    private Exception? _failure;
    private CountingStreamPipeWriter? _writer;
    private SendLimitedPipeWriter? _bodyWriter;

    internal bool IsFaulted => _faulted;

    internal Exception? Failure => _failure;

    internal async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        if (_faulted)
        {
            _semaphore.Release();
            throw new IOException("The connection contains an incomplete JSON-RPC message.", _failure);
        }
    }

    /// <summary>Prevents future sends. The caller must hold the send lock.</summary>
    internal void Fault(Exception? failure = null)
    {
        if (_faulted) return;
        _failure = failure;
        _faulted = true;
    }

    internal void Release() => _semaphore.Release();

    /// <summary>Faults the connection when <paramref name="writer"/> holds more unsent bytes than allowed. The caller must hold the send lock.</summary>
    internal void EnsureWithinUnsentLimit(CountingStreamPipeWriter writer)
    {
        if (_failure is SocketSendLimitException failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        if (maxUnsentBytes <= 0 || writer.UnflushedBytes <= maxUnsentBytes)
        {
            return;
        }

        throw FaultWithLimit(new SocketSendLimitException($"The unsent JSON-RPC response of {writer.UnflushedBytes} bytes is above JsonRpc.MaxUnsentWebSocketResponseSize = {maxUnsentBytes}."));
    }

    internal PipeWriter GetBodyWriter(CountingStreamPipeWriter writer)
    {
        if (maxUnsentBytes <= 0)
        {
            return writer;
        }

        if (_bodyWriter is null || _bodyWriter.Inner != writer)
        {
            _bodyWriter = new SendLimitedPipeWriter(this, writer);
        }

        _bodyWriter.Sync();
        return _bodyWriter;
    }

    internal async ValueTask<FlushResult> FlushAsync(CountingStreamPipeWriter writer, CancellationToken cancellationToken)
    {
        using CancellationTokenSource? deadline = StartSendDeadline(writer.UnflushedBytes, cancellationToken);
        try
        {
            return await writer.FlushAsync(deadline?.Token ?? cancellationToken);
        }
        catch (OperationCanceledException ex) when (deadline is not null && !cancellationToken.IsCancellationRequested)
        {
            throw SendDeadlineExceeded(ex);
        }
    }

    internal SocketSendLimitException SendDeadlineExceeded(OperationCanceledException cancellation) =>
        FaultWithLimit(new SocketSendLimitException($"The JSON-RPC response was not sent before the send deadline of JsonRpc.Timeout = {sendTimeout} ms plus the time for its size.", cancellation));

    /// <summary>Returns a token that is cancelled when a send of <paramref name="bytes"/> takes too long, or <c>null</c> when sends have no deadline.</summary>
    internal CancellationTokenSource? StartSendDeadline(long bytes, CancellationToken cancellationToken)
    {
        if (maxUnsentBytes <= 0 || sendTimeout <= 0)
        {
            return null;
        }

        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter((int)Math.Min(int.MaxValue - 1, sendTimeout + Math.Min(bytes * 1000 / MinSendBytesPerSecond, MaxSendExtensionMilliseconds)));
        return deadline;
    }

    private SocketSendLimitException FaultWithLimit(SocketSendLimitException exception)
    {
        Fault(exception);
        return exception;
    }

    /// <summary>Returns the connection's response writer, reset to <paramref name="writtenCount"/>, or a new one if it was completed. The caller must hold the send lock.</summary>
    internal CountingStreamPipeWriter GetWriter(Stream stream, StreamPipeWriterOptions options, long writtenCount)
    {
        if (_writer is null || _writer.InnerStream != stream || _writer.IsCompleted)
        {
            _writer = new CountingStreamPipeWriter(stream, options, writtenCount, flushOnThreshold: false);
        }
        else
        {
            _writer.Reset(writtenCount);
        }

        return _writer;
    }

    /// <inheritdoc/>
    /// <remarks>Completes the response writer without writing its unflushed data, unless a send still holds the lock.</remarks>
    public void Dispose()
    {
        if (_semaphore.Wait(0))
        {
            _writer?.Complete(new ObjectDisposedException(nameof(SocketSendLock)));
            _writer = null;
            _bodyWriter = null;
            _semaphore.Release();
        }

        _semaphore.Dispose();
    }
}
