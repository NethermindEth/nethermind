// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Sockets;

public sealed class WebSocketMessageStream : Stream, IMessageBorderPreservingStream
{
    private readonly WebSocket _socket;
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
        => await ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_socket.State is WebSocketState.Closed or WebSocketState.CloseReceived or WebSocketState.CloseSent)
        {
            return 0;
        }

        ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Remote close", cancellationToken);
            return 0;
        }

        return result.Count;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_socket.State != WebSocketState.Open) { ThrowSocketNotOpen(); }

        await _socket.SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: false, cancellationToken);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private void ThrowSocketNotOpen() => throw new IOException($"WebSocket not open ({_socket.State})");

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public async ValueTask<int> WriteEndOfMessageAsync()
    {
        await _socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true, CancellationToken.None);
        return 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_socket is null, _socket);

    public async ValueTask<ReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open) return default;

        try
        {
            ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            return new ReceiveResult()
            {
                Closed = result.MessageType == WebSocketMessageType.Close,
                Read = result.Count,
                EndOfMessage = result.EndOfMessage
            };
        }
        catch (Exception innerException)
        {
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

            return new ReceiveResult() { Closed = true };
        }
    }
}
