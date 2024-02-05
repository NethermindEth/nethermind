// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

public class WebSocketMessageStream : Stream, IMessageBorderPreservingStream
{
    private WebSocket _socket;
    private readonly ILogger _logger;

    public WebSocketMessageStream(WebSocket socket, ILogManager logManager)
    {
        _socket = socket;
        _logger = logManager.GetClassLogger<WebSocketMessageStream>();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _ = _socket ?? throw new InvalidOperationException($"The underlying {nameof(WebSocket)} is null");

        if (_socket.State is WebSocketState.Closed or WebSocketState.CloseReceived or WebSocketState.CloseSent)
        {
            return 0;
        }

        ArraySegment<byte> segment = new(buffer, offset, count);
        WebSocketReceiveResult result = await _socket.ReceiveAsync(segment, cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Remote close", cancellationToken);
            return 0;
        }

        return result.Count;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _ = _socket ?? throw new ArgumentNullException(nameof(_socket));
        if (_socket.State != WebSocketState.Open) { throw new IOException($"WebSocket not open ({_socket.State})"); }

        ArraySegment<byte> segment = new(buffer, offset, count);
        await _socket.SendAsync(segment, WebSocketMessageType.Text, false, cancellationToken);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public async Task<int> WriteEndOfMessageAsync()
    {
        await _socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true, CancellationToken.None);
        return 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_socket is null, _socket);
    }

    public async Task<ReceiveResult?> ReceiveAsync(ArraySegment<byte> buffer)
    {
        ReceiveResult? result = null;
        if (_socket.State == WebSocketState.Open)
        {
            Task<WebSocketReceiveResult> resultTask = _socket.ReceiveAsync(buffer, CancellationToken.None);

            await resultTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Exception? innerException = t.Exception;
                    while (innerException?.InnerException is not null)
                    {
                        innerException = innerException.InnerException;
                    }

                    if (innerException is SocketException socketException)
                    {
                        if (socketException.SocketErrorCode == SocketError.ConnectionReset)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Client disconnected: {innerException.Message}.");
                        }
                        else
                        {
                            if (_logger.IsInfo) _logger.Info($"Not able to read from WebSockets ({socketException.SocketErrorCode}: {socketException.ErrorCode}). {innerException.Message}");
                        }
                    }
                    else if (innerException is WebSocketException webSocketException)
                    {
                        if (webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Client disconnected: {innerException.Message}.");
                        }
                        else
                        {
                            if (_logger.IsInfo) _logger.Info($"Not able to read from WebSockets ({webSocketException.WebSocketErrorCode}: {webSocketException.ErrorCode}). {innerException.Message}");
                        }
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"Not able to read from WebSockets. {innerException?.Message}");
                    }

                    result = new WebSocketsReceiveResult() { Closed = true };
                }

                if (t.IsCompletedSuccessfully)
                {
                    result = new WebSocketsReceiveResult()
                    {
                        Closed = t.Result.MessageType == WebSocketMessageType.Close,
                        Read = t.Result.Count,
                        EndOfMessage = t.Result.EndOfMessage,
                        CloseStatus = t.Result.CloseStatus,
                        CloseStatusDescription = t.Result.CloseStatusDescription
                    };
                }
            });
        }

        return result;
    }

    //public Task CloseAsync(ReceiveResult? result)
    //{
    //    if (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
    //    {
    //        return _socket.CloseAsync(result is WebSocketsReceiveResult { CloseStatus: { } } r ? r.CloseStatus.Value : WebSocketCloseStatus.Empty,
    //            result?.CloseStatusDescription,
    //            CancellationToken.None);
    //    }

    //    if (_socket.State is WebSocketState.CloseReceived)
    //    {
    //        return _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, result?.CloseStatusDescription,
    //            CancellationToken.None);
    //    }

    //    return Task.CompletedTask;
    //}
}
