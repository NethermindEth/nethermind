// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

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
        if (maxUnsentBytes <= 0 || writer.UnflushedBytes <= maxUnsentBytes)
        {
            return;
        }

        IOException exception = new($"The unsent JSON-RPC response of {writer.UnflushedBytes} bytes is above JsonRpc.MaxUnsentWebSocketResponseSize = {maxUnsentBytes}.");
        Fault(exception);
        throw exception;
    }

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
            _semaphore.Release();
        }

        _semaphore.Dispose();
    }
}
