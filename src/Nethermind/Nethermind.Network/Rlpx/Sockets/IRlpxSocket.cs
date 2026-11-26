// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.Rlpx.Sockets;

/// <summary>
/// The byte stream an RLPx connection runs on.
/// </summary>
/// <remarks>A seam for tests, which need short sends, held reads and injected errors that a real socket can't produce on demand.</remarks>
internal interface IRlpxSocket : IDisposable
{
    /// <summary>Receives into <paramref name="buffer"/>; an empty buffer waits for data without taking any.</summary>
    /// <returns>The number of bytes received, or zero at the end of the stream for a non-empty buffer.</returns>
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Sends some or all of <paramref name="buffer"/>.</summary>
    /// <returns>The number of bytes sent, which may be fewer than requested.</returns>
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Stops both directions and releases the socket.</summary>
    void Close();
}

/// <summary>An <see cref="IRlpxSocket"/> over a connected TCP socket.</summary>
internal sealed class TcpRlpxSocket(Socket socket) : IRlpxSocket
{
    public Socket Socket { get; } = socket;

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => Socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        => Socket.SendAsync(buffer, SocketFlags.None, cancellationToken);

    public void Close() => Socket.Dispose();

    public void Dispose() => Socket.Dispose();
}
