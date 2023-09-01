// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

public class WebSocketStream : Stream
{
    private WebSocket? _socket;

    public WebSocketStream(WebSocket socket)
    {
        _socket = socket;
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
        _ = _socket ?? throw new ArgumentNullException(nameof(_socket));

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
        await _socket.SendAsync(segment, WebSocketMessageType.Binary, true, cancellationToken);
    }

    public Task CloseAsync()
    {
        return CloseAsync(CancellationToken.None);
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _ = _socket ?? throw new ArgumentNullException(nameof(_socket));

        return _socket.State == WebSocketState.Closed
            ? Task.CompletedTask
            : _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Local close", cancellationToken);
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

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && _socket != null) { _socket.Dispose(); }
        }
        finally
        {
            _socket = null;
            base.Dispose(disposing);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_socket == null) throw new ObjectDisposedException(nameof(_socket));
    }
}
